using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using StickyTodo;

static partial class Program
{
    static void CheckDetailsCopy()
    {
        var task = new TaskItem { Title = "Copy title Ω", UpdatedAt = Baseline };
        var originalTimestamp = task.UpdatedAt;
        var writes = new List<string>();
        var session = new DetailsSaveSession("Initial details", writes.Add) { AutoSaveIntervalSeconds = 0 };
        var note = new DetailsWindow(task, session);
        try
        {
            note.Show();
            PumpDispatcher(TimeSpan.FromMilliseconds(10));
            var copyButton = Find<Button>(note, "CopyButton");
            Check(System.Windows.Automation.AutomationProperties.GetName(copyButton) == "Copy task title and details",
                "copy button has an accessible name");
            var editor = Find<TextBox>(note, "Editor");
            editor.Text = "Unsaved Żółć ☁\nSecond line";
            copyButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(Clipboard.GetText(TextDataFormat.UnicodeText) == $"{task.Title}\n\n{editor.Text}",
                "copy button puts the title and unsaved Unicode details on the clipboard");
            Check(session.IsDirty && writes.Count == 0 && task.UpdatedAt == originalTimestamp,
                "copying unsaved details does not save or change the task timestamp");

            editor.Text = "";
            copyButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(Clipboard.GetText(TextDataFormat.UnicodeText) == $"{task.Title}\n\n",
                "copying empty details preserves the title separator");
            Check(session.IsDirty && writes.Count == 0 && task.UpdatedAt == originalTimestamp,
                "copying empty details leaves the edit pending without changing its timestamp");

            session.Save(force: true);
            writes.Clear();
            var refreshedTask = new TaskItem { Title = "Refreshed title", UpdatedAt = Baseline };
            var refreshedTimestamp = refreshedTask.UpdatedAt;
            note.RefreshTask(refreshedTask, "Refreshed details ☃");
            copyButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(Clipboard.GetText(TextDataFormat.UnicodeText) == $"{refreshedTask.Title}\n\nRefreshed details ☃",
                "copy uses the current task and details after a sync refresh");
            Check(!session.IsDirty && writes.Count == 0 && refreshedTask.UpdatedAt == refreshedTimestamp,
                "copying refreshed details does not save or touch the refreshed task");
        }
        finally { note.Close(); }
    }

    static void CheckConfigurableAutoSave()
    {
        foreach (var interval in new[] { 1, 7, 120, 3600 })
        {
            var now = Baseline;
            var writes = new List<string>();
            var session = new DetailsSaveSession("", writes.Add, () => now) { AutoSaveIntervalSeconds = interval };
            session.Edit("First");
            session.Save();
            Check(writes.Count == 1, $"first edit saves immediately with a {interval}-second interval");
            now = now.AddSeconds(interval).AddTicks(-1);
            session.Edit("Pending");
            session.Save();
            Check(writes.Count == 1 && session.SaveDelay == TimeSpan.FromTicks(1), "edits wait until the configured deadline");
            now = now.AddTicks(1);
            session.Edit("Latest text");
            session.Save();
            Check(writes.Count == 2 && writes[^1] == "Latest text", "editing at the deadline does not postpone saving");
        }

        var clock = Baseline;
        var saved = new List<string>();
        var configurable = new DetailsSaveSession("", saved.Add, () => clock) { AutoSaveIntervalSeconds = 0 };
        configurable.Edit("Disabled");
        configurable.Save();
        clock = clock.AddHours(2);
        configurable.Save();
        Check(saved.Count == 0 && configurable.IsDirty && configurable.SaveDelay == null, "disabled autosave has no deadline and never writes automatically");
        configurable.Save(force: true);
        Check(saved.SequenceEqual(new[] { "Disabled" }) && !configurable.IsDirty, "forced saves work with autosave disabled");
        configurable.Edit("Pending");
        configurable.AutoSaveIntervalSeconds = 120;
        clock = clock.AddSeconds(30);
        Check(configurable.SaveDelay == TimeSpan.FromSeconds(90), "re-enabling preserves the last successful save time");
        configurable.AutoSaveIntervalSeconds = 3600;
        Check(configurable.SaveDelay == TimeSpan.FromSeconds(3570), "lengthening the interval keeps the existing save history");
        configurable.AutoSaveIntervalSeconds = 10;
        configurable.Save();
        Check(saved.Count == 2 && !configurable.IsDirty, "shortening an elapsed interval makes pending text eligible immediately");
        configurable.AutoSaveIntervalSeconds = 0;
        configurable.Edit("Still disabled");
        configurable.Save();
        Check(saved.Count == 2 && configurable.IsDirty, "disabling also suppresses subsequent saves");
        configurable.Discard();
        configurable.Save(force: true);
        Check(saved.Count == 2, "discarded disabled sessions cannot be force-saved");

        bool fail = false;
        var retry = new DetailsSaveSession("", text =>
        {
            if (fail) throw new IOException("Test failure");
            saved.Add(text);
        }, () => clock) { AutoSaveIntervalSeconds = 7 };
        retry.Edit("Before failure");
        retry.Save();
        clock = clock.AddSeconds(7);
        retry.Edit("Retry text");
        fail = true;
        ExpectFailure<IOException>(() => retry.Save(), "configured autosave failure");
        Check(retry.IsDirty && retry.SaveDelay == TimeSpan.Zero && retry.Text == "Retry text", "failure preserves dirty text without advancing the save time");
        fail = false;
        retry.Save();
        Check(!retry.IsDirty && saved[^1] == "Retry text" && retry.SaveDelay == TimeSpan.FromSeconds(7), "successful retry starts a new configured interval");
    }

    static void CheckDetailsIndicatorsAndSettings() => WithCleanStorage(() =>
    {
        var settingsBackup = Backup(Storage.SettingsPath);
        MainWindow? main = null;
        try
        {
            Storage.SaveSettings(new AppearanceSettings { DetailsAutoSaveIntervalSeconds = 0 });
            var initial = new[] { "Missing", "Empty", "Whitespace", "Populated" }
                .Select(title => new TaskItem { Title = title, UpdatedAt = Baseline }).ToArray();
            Storage.SaveTasks(initial);
            Storage.SaveDetails(initial[1].Id, "");
            Storage.SaveDetails(initial[2].Id, " \t\r\n");
            Storage.SaveDetails(initial[3].Id, "Notes Żółć");
            var loaded = Storage.LoadTasks();
            Check(loaded.Select(t => t.HasDetails).SequenceEqual(new[] { false, false, false, true }), "loading distinguishes missing, empty, whitespace-only and populated details");
            Check(loaded.All(t => t.UpdatedAt == Baseline), "loading the details indicator preserves timestamps");
            Check(!File.ReadAllText(Storage.TasksPath).Contains("hasDetails"), "details indicators are not task metadata");

            main = new MainWindow(enableGoogle: false);
            main.Show();
            var tasks = (ObservableCollection<TaskItem>)typeof(MainWindow).GetField("_tasks", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
            var task = tasks[0];
            var root = (FrameworkElement)((DataTemplate)main.FindResource("TaskTemplate")).LoadContent();
            root.DataContext = task;
            void CheckIcon(bool hasDetails)
            {
                PumpDispatcher(TimeSpan.FromMilliseconds(10));
                Check(task.HasDetails == hasDetails && Find<System.Windows.Shapes.Path>(root, "DetailsIcon").Visibility == (hasDetails ? Visibility.Visible : Visibility.Collapsed) &&
                      Find<TextBlock>(root, "AddDetailsIcon").Visibility == (hasDetails ? Visibility.Collapsed : Visibility.Visible), "details template follows current content");
            }
            CheckIcon(false);
            Invoke<object?>(main, "OpenDetails", task);
            var notes = (Dictionary<Guid, DetailsWindow>)typeof(MainWindow).GetField("_detailsWindows", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
            var sessions = (Dictionary<Guid, DetailsSaveSession>)typeof(MainWindow).GetField("_detailsSessions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
            var note = notes[task.Id];
            var editor = Find<TextBox>(note, "Editor");
            var timer = (DispatcherTimer)typeof(DetailsWindow).GetField("_saveTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(note)!;
            editor.Text = "Unsaved text";
            CheckIcon(true);
            editor.Text = " \t\r\n";
            CheckIcon(false);
            Check(!timer.IsEnabled && !File.Exists(Storage.DetailsPath(task.Id)) && task.UpdatedAt == Baseline,
                "disabled edits update the icon without scheduling, writing, or touching the timestamp");

            ApplySettings(main, new AppearanceSettings { DetailsAutoSaveIntervalSeconds = 1 });
            Check(Storage.LoadDetails(task.Id) == editor.Text && !sessions[task.Id].IsDirty, "re-enabling autosave saves eligible pending text without trimming whitespace");
            editor.Text = "Autosaved";
            Check(timer.IsEnabled, "positive interval schedules subsequent edits");
            PumpDispatcher(TimeSpan.FromMilliseconds(1200));
            Check(Storage.LoadDetails(task.Id) == "Autosaved" && !timer.IsEnabled, "open note saves at the configured deadline and stops its timer");
            editor.Text = "Pending";
            ApplySettings(main, new AppearanceSettings { DetailsAutoSaveIntervalSeconds = 0 });
            Check(!timer.IsEnabled && sessions[task.Id].IsDirty, "disabling stops an already scheduled save without discarding text");
            ApplySettings(main, new AppearanceSettings { DetailsAutoSaveIntervalSeconds = 3600 });
            Check(timer.IsEnabled && timer.Interval > TimeSpan.FromMinutes(59), "lengthening reschedules an open note from its last save");

            _ = main.Dispatcher.BeginInvoke(new Action(() =>
            {
                var dialog = Application.Current.Windows.OfType<SettingsWindow>().Single(w => w.IsVisible);
                Find<TextBox>(dialog, "DetailsAutoSaveIntervalSeconds").Text = "0";
                Check(!timer.IsEnabled && sessions[task.Id].AutoSaveIntervalSeconds == 0, "settings preview disables an open note's timer");
                dialog.Close();
            }));
            Invoke<object?>(main, "Settings_Click", main, new RoutedEventArgs());
            Check(sessions[task.Id].AutoSaveIntervalSeconds == 3600 && timer.IsEnabled && timer.Interval > TimeSpan.FromMinutes(59),
                "Cancel restores the prior interval and pending timer without resetting history");
            Check(Storage.LoadSettings().DetailsAutoSaveIntervalSeconds == 0, "Cancel leaves persisted settings unchanged");

            double noteWidth = note.Width, noteHeight = note.Height;
            ApplySettings(main, new AppearanceSettings { DetailsAutoSaveIntervalSeconds = 0, ZoomPercent = 50 });
            Check(Find<DockPanel>(note, "RootPanel").LayoutTransform is System.Windows.Media.ScaleTransform { ScaleX: 0.5 } &&
                  Math.Abs(note.Width - noteWidth / 2) < 1 && Math.Abs(note.Height - noteHeight / 2) < 1, "zoom propagates to open details windows");
            ApplySettings(main, new AppearanceSettings { DetailsAutoSaveIntervalSeconds = 0 });
            Check(Math.Abs(note.Width - noteWidth) < 1 && Math.Abs(note.Height - noteHeight) < 1, "details windows return to their size at 100%");
            editor.Text = "Saved on close";
            note.Close();
            Check(Storage.LoadDetails(task.Id) == "Saved on close", "closing a note forces a save with autosave disabled");
            var retained = sessions[task.Id];
            Invoke<object?>(main, "OpenDetails", task);
            Check(ReferenceEquals(retained, sessions[task.Id]), "reopening keeps the configured session");
            Find<TextBox>(notes[task.Id], "Editor").Text = "";
            CheckIcon(false);
            main.Close();
            main = null;
            Check(Storage.LoadDetails(task.Id) == "" && !Storage.LoadTasks().Single(t => t.Id == task.Id).HasDetails,
                "application close saves cleared details with autosave disabled and reload shows plus");
        }
        finally
        {
            main?.Close();
            Restore(Storage.SettingsPath, settingsBackup);
        }
    });
}
