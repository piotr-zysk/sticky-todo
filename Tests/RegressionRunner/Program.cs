using System.Collections.ObjectModel;
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

    static void CheckPriorityTemplate(MainWindow window)
    {
        var template = (DataTemplate)window.FindResource("TaskTemplate");
        var root = (FrameworkElement)template.LoadContent();
        var title = (TextBlock)template.FindName("TaskTitle", root)!;
        var highTrigger = title.Style.Triggers.OfType<DataTrigger>().Single(trigger =>
            trigger.Binding is Binding binding && binding.Path?.Path == nameof(TaskItem.IsHigh));
        Check(highTrigger.Value is true && highTrigger.Setters.OfType<Setter>().Any(setter => setter.Property == TextBlock.FontWeightProperty),
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