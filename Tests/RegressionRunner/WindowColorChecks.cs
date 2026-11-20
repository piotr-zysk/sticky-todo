using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using StickyTodo;

static partial class Program
{
    static void CheckWindowColors() => WithCleanStorage(() =>
    {
        var settingsBackup = Backup(Storage.SettingsPath);
        MainWindow? main = null;
        try
        {
            var root = new TaskItem { Title = "Colored root", UpdatedAt = Baseline };
            var child = new TaskItem { Title = "Child", ParentId = root.Id, UpdatedAt = Baseline };
            var leaf = new TaskItem { Title = "Leaf", ParentId = child.Id, UpdatedAt = Baseline };
            var sibling = new TaskItem { Title = "Sibling", ParentId = root.Id, UpdatedAt = Baseline };
            var other = new TaskItem { Title = "Other root", UpdatedAt = Baseline };
            root.Children.Add(child);
            root.Children.Add(sibling);
            child.Children.Add(leaf);
            Storage.SaveTasks(new[] { root, other });
            var rootKey = root.Id.ToString("N");
            var childKey = child.Id.ToString("N");
            var missingKey = Guid.NewGuid().ToString("N");
            File.WriteAllText(Storage.SettingsPath, $"backgroundColor: '#ABCDEF'\nwindowBackgroundColors:\n  '{root.Id}': '#aabbcc'\n  nope: '#123456'\n  '{Guid.NewGuid():N}': invalid\n  '{Guid.Empty}': '#123456'\n");
            var loaded = Storage.LoadSettings();
            Check(loaded.BackgroundColor == "#ABCDEF" && loaded.WindowBackgroundColors.Count == 1 &&
                  loaded.WindowBackgroundColors[rootKey] == "#AABBCC", "bad override entries do not discard valid colors or other settings");
            File.WriteAllText(Storage.SettingsPath, "backgroundColor: '#ABCDEF'\n");
            Check(Storage.LoadSettings().WindowBackgroundColors.Count == 0, "older settings default to no window overrides");
            var settings = new AppearanceSettings
            {
                DetailsAutoSaveIntervalSeconds = 0,
                WindowBackgroundColors = new() { [rootKey] = "#202020", [childKey] = "#FFFFFF", [missingKey] = "#123456" }
            };
            Storage.SaveSettings(settings);
            Check(Storage.LoadSettings().WindowBackgroundColors.Count == 3, "window overrides round-trip including absent task IDs");
            var copy = settings.Copy();
            copy.WindowBackgroundColors.Remove(rootKey);
            Check(settings.WindowBackgroundColors.ContainsKey(rootKey), "settings copies do not share the overrides map");
            var global = new SettingsWindow(settings, _ => { });
            try
            {
                global.Settings.WindowBackgroundColors.Remove(rootKey);
                Check(settings.WindowBackgroundColors.ContainsKey(rootKey), "global settings edits do not mutate the original map");
                Invoke<object?>(global, "Reset_Click", global, new RoutedEventArgs());
                Check(global.Settings.WindowBackgroundColors.ContainsKey(childKey), "global Reset all preserves window colors");
            }
            finally { global.Close(); }

            main = new MainWindow(enableGoogle: false);
            main.Show();
            root = MainTasks(main).Single(t => t.Id == root.Id);
            child = root.Children.Single(t => t.Id == child.Id);
            leaf = child.Children.Single();
            sibling = root.Children.Single(t => t.Id == sibling.Id);
            other = MainTasks(main).Single(t => t.Id == other.Id);
            foreach (var task in new[] { root, child, leaf, sibling, other }) Invoke<object?>(main, "OpenDetails", task);
            var notes = Notes(main);
            static string BackgroundOf(Window window) => ((SolidColorBrush)window.Background).Color.ToString();
            Check(BackgroundOf(notes[root.Id]) == "#FF202020" && BackgroundOf(notes[sibling.Id]) == "#FF202020" &&
                  BackgroundOf(notes[child.Id]) == "#FFFFFFFF" && BackgroundOf(notes[leaf.Id]) == "#FFFFFFFF" &&
                  BackgroundOf(notes[other.Id]) == "#FFF9EDB5", "nearest override wins across multiple levels without affecting unrelated roots");
            Check(((SolidColorBrush)notes[root.Id].Foreground).Color == Colors.White &&
                  ((SolidColorBrush)Find<TaskListControl>(notes[root.Id], "ChildView").Resources["AppBackground"]).Color == Color.FromRgb(32, 32, 32),
                  "dark window colors update contrast and child-list popup resources");

            void Edit(TaskItem task, Action<WindowColorSettingsWindow> action)
            {
                Exception? failure = null;
                _ = main.Dispatcher.BeginInvoke(new Action(() =>
                {
                    var dialog = Application.Current.Windows.OfType<WindowColorSettingsWindow>().Single(w => w.IsVisible);
                    try { action(dialog); }
                    catch (Exception ex) { failure = ex; }
                    finally { if (dialog.IsVisible) dialog.Close(); }
                }));
                Find<Button>(notes[task.Id], "SettingsButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (failure != null) throw new InvalidOperationException("Window color dialog check failed.", failure);
            }
            static void Click(WindowColorSettingsWindow dialog, string name) =>
                Find<Button>(dialog, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            var beforeTasks = File.ReadAllText(Storage.TasksPath);
            var beforeSync = JsonSerializer.Serialize(Storage.LoadSnapshot());
            Edit(root, dialog =>
            {
                Find<TextBox>(dialog, "HexColor").Text = "#GGGGGG";
                Check(!Find<Button>(dialog, "SaveButton").IsEnabled, "invalid hex disables saving");
                Find<TextBox>(dialog, "HexColor").Text = "#112233";
                Check(BackgroundOf(notes[root.Id]) == "#FF112233" && BackgroundOf(notes[sibling.Id]) == "#FF112233" &&
                      BackgroundOf(notes[leaf.Id]) == "#FFFFFFFF", "live ancestor preview respects descendant overrides");
            });
            Check(BackgroundOf(notes[root.Id]) == "#FF202020" && Storage.LoadSettings().WindowBackgroundColors[rootKey] == "#202020",
                  "closing settings restores appearance and leaves disk unchanged");
            Edit(root, dialog =>
            {
                Find<Slider>(dialog, "RedSlider").Value = 50;
                Click(dialog, "SaveButton");
            });
            Check(Storage.LoadSettings().WindowBackgroundColors[rootKey] == "#322020" &&
                  Storage.LoadSettings().RecentColors.Count == 0, "Save persists RGB edits without remembering recent colors");
            Edit(child, dialog =>
            {
                Click(dialog, "ClearButton");
                Check(BackgroundOf(notes[child.Id]) == "#FF322020" && BackgroundOf(notes[leaf.Id]) == "#FF322020",
                      "Clear previews inherited color throughout the child branch");
            });
            Check(BackgroundOf(notes[leaf.Id]) == "#FFFFFFFF", "Cancel after Clear restores explicit child color and descendants");
            Edit(child, dialog => { Click(dialog, "ClearButton"); Click(dialog, "SaveButton"); });
            Check(!Storage.LoadSettings().WindowBackgroundColors.ContainsKey(childKey) && BackgroundOf(notes[leaf.Id]) == "#FF322020",
                  "saving Clear removes only the selected override");
            var note = notes[root.Id];
            Find<Button>(note, "ViewButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(!note.IsListView && BackgroundOf(note) == "#FF322020", "text and list modes share their background");
            note.Close();
            notes[child.Id].Close();
            notes[leaf.Id].Close();
            Invoke<object?>(main, "OpenDetails", leaf);
            Check(BackgroundOf(notes[leaf.Id]) == "#FF322020", "inheritance works when ancestor windows are closed");
            Invoke<object?>(main, "OpenDetails", root);

            var savedSettings = File.ReadAllText(Storage.SettingsPath);
            Edit(root, dialog =>
            {
                Find<TextBox>(dialog, "HexColor").Text = "#445566";
                using (var locked = new FileStream(Storage.SettingsPath, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    var save = (Action<string?>)typeof(WindowColorSettingsWindow).GetField("_save", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(dialog)!;
                    ExpectStorageFailure(() => save("#445566"));
                }
                Check(dialog.IsVisible && Find<TextBox>(dialog, "HexColor").Text == "#445566" &&
                      File.ReadAllText(Storage.SettingsPath) == savedSettings, "failed save preserves the draft and persisted settings");
            });
            Check(BackgroundOf(notes[root.Id]) == "#FF322020", "Cancel after save failure restores committed color");
            Edit(root, dialog => { Find<TextBox>(dialog, "HexColor").Text = "#445566"; Click(dialog, "SaveButton"); });
            Check(Storage.LoadSettings().WindowBackgroundColors[rootKey] == "#445566", "save succeeds after storage becomes writable again");

            var snapshot = Storage.LoadSnapshot();
            Invoke<object?>(main, "ApplySyncedTasks", snapshot);
            Check(BackgroundOf(notes[leaf.Id]) == "#FF445566" && notes[root.Id].IsListView &&
                  File.ReadAllText(Storage.TasksPath) == beforeTasks && JsonSerializer.Serialize(Storage.LoadSnapshot()) == beforeSync,
                  "sync rebinding preserves local overrides without touching task timestamps or remote data");
            notes[root.Id].SetCompletionState(false, true);
            Check(!Find<Button>(notes[root.Id], "SettingsButton").IsEnabled, "pending completion disables color settings");
            notes[root.Id].SetCompletionState(true, false);
            Check(!Find<Button>(notes[root.Id], "SettingsButton").IsEnabled, "ancestor completion lock disables color settings");
            notes[root.Id].SetCompletionState(false, false);

            main.Close();
            main = new MainWindow(enableGoogle: false);
            main.Show();
            root = MainTasks(main).Single(t => t.Id == root.Id);
            Invoke<object?>(main, "OpenDetails", root);
            Check(BackgroundOf(Notes(main)[root.Id]) == "#FF445566", "saved overrides survive restarting the app");
        }
        finally
        {
            main?.Close();
            Restore(Storage.SettingsPath, settingsBackup);
        }
    });
}
