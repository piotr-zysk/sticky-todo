using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace StickyTodo;

public static class Choices
{
    public static readonly int[] Hours = Enumerable.Range(0, 24).ToArray();
    public static readonly int[] Minutes = Enumerable.Range(0, 12).Select(i => i * 5).ToArray();
}

public partial class MainWindow : Window
{
    readonly ObservableCollection<TaskItem> _tasks = new();
    readonly Dictionary<Guid, DetailsWindow> _detailsWindows = new();
    readonly Dictionary<Guid, DetailsSaveSession> _detailsSessions = new();
    AppearanceSettings _settings = new();
    readonly DispatcherTimer _overdueTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    const double BaseMinWidth = 320;
    double _zoom = 1, _unzoomedWidth = double.NaN, _appliedWidth = double.NaN;
    bool _applyingZoom;
    readonly GoogleConnection? _google;
    readonly Func<IDriveTransport>? _driveTransportFactory;
    bool _syncInProgress, _closeInProgress, _allowClose;
    internal AppearanceSettings Settings => _settings;
    internal Func<TaskItem, int, bool>? CompletionConfirmation { get; set; }
    IEnumerable<TaskItem> AllTasks => TaskTree.Flatten(_tasks);

    public string AppVersion => typeof(MainWindow).Assembly.GetName().Version?.ToString(3)
                                ?? throw new InvalidOperationException("The app assembly version is missing.");

    public MainWindow(bool enableGoogle = true, GoogleConnection? google = null, Func<IDriveTransport>? driveTransportFactory = null)
    {
        InitializeComponent();
        VersionText.Text = AppVersion;
        _driveTransportFactory = driveTransportFactory;
        if (enableGoogle)
        {
            _google = google ?? new GoogleConnection();
            _google.Changed += UpdateSyncStatus;
        }
        UpdateSyncStatus();
        _settings = Storage.LoadSettings();
        ApplyAppearance(_settings);
        foreach (var t in Storage.LoadTasks()) Attach(t, false);
        TaskView.Configure(this, _tasks);
        _tasks.CollectionChanged += (_, _) =>
        {
            UpdateListHeight();
            Dispatcher.BeginInvoke(UpdateListHeight, DispatcherPriority.Loaded);
        };
        _overdueTimer.Tick += (_, _) => { foreach (var t in AllTasks) t.RefreshOverdue(); };
        _overdueTimer.Start();
        Closing += OnClosing;
        UpdateListHeight();
    }

    void UpdateListHeight()
    {
        TaskView.UpdateListHeight();
    }

    // Resizing by the user makes WPF drop SizeToContent; restore it so the height keeps following the items.
    void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Remember user resizes in unzoomed units so previewing a large zoom and returning keeps the width.
        if (e.WidthChanged && !_applyingZoom && !(Math.Abs(e.NewSize.Width - _appliedWidth) < 1)) _unzoomedWidth = e.NewSize.Width / _zoom;
        if (SizeToContent != SizeToContent.Height)
            Dispatcher.BeginInvoke(() => SizeToContent = SizeToContent.Height, DispatcherPriority.Loaded);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ((HwndSource)PresentationSource.FromVisual(this)).AddHook(LockVerticalResize);
    }

    // Only the left/right edges resize the window (WindowChrome's own resize border is disabled).
    IntPtr LockVerticalResize(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_NCHITTEST = 0x0084, HTCLIENT = 1, HTLEFT = 10, HTRIGHT = 11, EdgeWidth = 5;
        if (msg != WM_NCHITTEST) return IntPtr.Zero;
        int x = (short)((long)lParam & 0xFFFF);
        var local = PointFromScreen(new Point(x, 0)).X;
        handled = true;
        if (local < EdgeWidth) return (IntPtr)HTLEFT;
        if (local > ActualWidth - EdgeWidth) return (IntPtr)HTRIGHT;
        return (IntPtr)HTCLIENT;
    }

    void Attach(TaskItem t, bool top)
    {
        Register(t);
        if (top) _tasks.Insert(0, t); else _tasks.Add(t);
    }

    void Register(TaskItem task)
    {
        foreach (var item in TaskTree.Flatten(new[] { task }))
        {
            item.PropertyChanged += OnItemChanged;
            item.ConfirmCompletion = ConfirmTaskCompletion;
        }
    }

    internal void AddTask(TaskItem task, bool top)
    {
        if (task.ParentId is not { } parentId) { Attach(task, top); return; }
        var parent = AllTasks.Single(t => t.Id == parentId);
        if (parent.IsDone || parent.IsBlocked) throw new InvalidOperationException("This task is pending completion.");
        Register(task);
        if (top) parent.Children.Insert(0, task); else parent.Children.Add(task);
    }

    bool ConfirmTaskCompletion(TaskItem task)
    {
        if (task.IsBlocked) return false;
        CommitAllEdits();
        var count = TaskTree.Flatten(task.Children).Count(t => !t.IsDone);
        return count == 0 || (CompletionConfirmation?.Invoke(task, count) ??
            MessageBox.Show(this,
                $"\"{task.Title}\" has {count} unfinished subtask(s), including nested subtasks. Completing it will delete all its subtasks and their text details after the undo delay.\n\nComplete this task?",
                "Unfinished subtasks", MessageBoxButton.YesNo, MessageBoxImage.Warning,
                MessageBoxResult.No) == MessageBoxResult.Yes);
    }

    void RefreshCompletionLocks()
    {
        var all = AllTasks.ToDictionary(t => t.Id);
        foreach (var task in all.Values)
        {
            var parentId = task.ParentId;
            var blocked = false;
            while (parentId is { } id)
            {
                var parent = all[id];
                blocked |= parent.IsDone;
                parentId = parent.ParentId;
            }
            task.IsBlocked = blocked;
            if (blocked) task.RemoveTimer?.Stop();
            else if (task.IsDone && task.RemoveTimer != null && !task.RemoveTimer.IsEnabled) task.RemoveTimer.Start();
            if (_detailsWindows.TryGetValue(task.Id, out var note)) note.SetCompletionState(blocked, task.IsDone);
        }
    }

    void Save() => Storage.SaveTasks(_tasks);
    internal void SaveAll() => Save();
    internal void ShowDetails(TaskItem task) => OpenDetails(task);

    void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not TaskItem t) return;
        switch (e.PropertyName)
        {
            case nameof(TaskItem.IsDone):
                Save();
                if (t.IsDone)
                {
                    var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(_settings.ItemDeleteRetentionSeconds) };
                    timer.Tick += (_, _) =>
                    {
                        if (t.IsBlocked || _syncInProgress) return;
                        timer.Stop();
                        t.RemoveTimer = null;
                        Complete(t);
                    };
                    t.RemoveTimer = timer;
                    timer.Start();
                }
                else
                {
                    t.RemoveTimer?.Stop();
                    t.RemoveTimer = null;
                }
                RefreshCompletionLocks();
                break;
            case nameof(TaskItem.Title):
            case nameof(TaskItem.IsHigh):
            case nameof(TaskItem.DueDate):
            case nameof(TaskItem.DueTime):
                Save();
                break;
        }
    }

    void Complete(TaskItem t)
    {
        if (!AllTasks.Contains(t) || t.IsBlocked) return;
        try
        {
            var removed = TaskTree.Flatten(new[] { t }).ToList();
            Storage.CompleteTask(t, _tasks);
            foreach (var item in removed)
            {
                item.RemoveTimer?.Stop();
                item.RemoveTimer = null;
                if (_detailsSessions.Remove(item.Id, out var session)) session.Discard();
                if (_detailsWindows.TryGetValue(item.Id, out var note)) note.DiscardAndClose();
                item.PropertyChanged -= OnItemChanged;
                item.ConfirmCompletion = null;
            }
            if (t.ParentId is { } parentId) AllTasks.Single(item => item.Id == parentId).Children.Remove(t);
            else _tasks.Remove(t);
            RefreshCompletionLocks();
        }
        catch (Exception ex)
        {
            t.IsDone = false;
            MessageBox.Show(this, $"Could not complete the task. Please retry.\n\n{ex.Message}", "Task completion failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    void PrepareLocalForSync()
    {
        CommitAllEdits();
        foreach (var session in _detailsSessions.Values) session.Save(force: true);
        foreach (var t in AllTasks.Where(x => x.IsDone && !x.IsBlocked).ToList())
        {
            t.RemoveTimer?.Stop();
            Complete(t);
            if (AllTasks.Contains(t)) throw new System.IO.IOException("Could not complete a checked task.");
        }
        Save();
    }

    async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        if (_syncInProgress || _closeInProgress) return;
        _closeInProgress = true;
        try
        {
            PrepareLocalForSync(); // Local failures must never offer "Close anyway".
            if (_google?.IsSignedIn == true)
            {
                while (true)
                {
                    try { await SynchronizeAsync(); break; }
                    catch (Exception ex) when (ex is not SyncLocalSaveException)
                    {
                        var dialog = new SyncFailureWindow(FriendlySyncError(ex), _zoom) { Owner = this };
                        dialog.ShowDialog();
                        if (dialog.Choice == SyncFailureChoice.Retry) continue;
                        if (dialog.Choice == SyncFailureChoice.CloseAnyway) break;
                        return;
                    }
                }
            }
            foreach (var note in _detailsWindows.Values.ToList()) note.Close();
            if (_detailsWindows.Count > 0) return;
            _overdueTimer.Stop();
            if (_google != null) _google.Changed -= UpdateSyncStatus;
            _allowClose = true;
            // Avoid recursively closing the window from within its first Closing event.
            _ = Dispatcher.BeginInvoke(Close);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not save local tasks. The application will stay open so you can retry.\n\n{ex.Message}",
                "Save failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { _closeInProgress = false; }
    }

    void UpdateSyncStatus()
    {
        SyncButton.Visibility = _google?.IsSignedIn == true ? Visibility.Visible : Visibility.Collapsed;
        SyncButton.Content = _syncInProgress ? "…" : "↻";
        SyncButton.IsEnabled = !_syncInProgress;
        SyncButton.ToolTip = _syncInProgress ? "Syncing with Google Drive…"
            : _google?.LastSync is { } last ? $"Last synced: {last.ToLocalTime():yyyy-MM-dd HH:mm:ss}" : "Never synced";
    }

    async void Sync_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_syncInProgress || _closeInProgress || _google?.IsSignedIn != true) return;
        try
        {
            PrepareLocalForSync();
            await SynchronizeAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Sync failed. Local changes are retained.\n\n{FriendlySyncError(ex)}",
                "Google Drive sync", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    static string FriendlySyncError(Exception ex) => ex is OperationCanceledException
        ? "Google sync timed out. Check your internet connection and retry."
        : ex is Google.Apis.Auth.OAuth2.Responses.TokenResponseException
            ? "Google authorization could not be refreshed. Sign out and sign in again in Settings." : ex.Message;

    sealed class SyncLocalSaveException(Exception inner) : System.IO.IOException(
        "Could not read or save local sync data. Please retry. " + inner.Message, inner);

    async Task SynchronizeAsync(IDriveTransport? transport = null)
    {
        if (_syncInProgress) throw new InvalidOperationException("A sync is already running.");
        _syncInProgress = true;
        RootDock.IsEnabled = false;
        var timers = AllTasks.Where(t => t.RemoveTimer?.IsEnabled == true).Select(t => t.RemoveTimer!).ToList();
        foreach (var timer in timers) timer.Stop();
        foreach (var note in _detailsWindows.Values) { note.IsSyncing = true; note.IsEnabled = false; }
        UpdateSyncStatus();
        try
        {
            SyncSnapshot local;
            try { local = Storage.LoadSnapshot(); }
            catch (Exception ex) { throw new SyncLocalSaveException(ex); }
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            transport ??= _driveTransportFactory?.Invoke();
            using var drive = transport == null ? _google!.CreateService() : null;
            var merged = await SyncEngine.MergeAndUploadAsync(local, transport ?? new GoogleDriveTransport(drive!), timeout.Token);
            try { Storage.ApplySnapshot(merged); }
            catch (Exception ex) { throw new SyncLocalSaveException(ex); }
            ApplySyncedTasks(merged);
            _google?.RecordSuccess();
        }
        finally
        {
            foreach (var note in _detailsWindows.Values) { note.IsSyncing = false; note.IsEnabled = true; }
            foreach (var task in AllTasks.Where(t => t.IsDone && !t.IsBlocked && t.RemoveTimer != null)) task.RemoveTimer!.Start();
            RootDock.IsEnabled = true;
            _syncInProgress = false;
            UpdateSyncStatus();
        }
    }

    void ApplySyncedTasks(SyncSnapshot snapshot)
    {
        foreach (var task in AllTasks) { task.PropertyChanged -= OnItemChanged; task.ConfirmCompletion = null; task.RemoveTimer?.Stop(); }
        _tasks.Clear();
        var items = snapshot.Tasks.Where(t => !t.CompletedAt.HasValue).Select(record =>
        {
            var task = Storage.ToTask(record);
            task.HasDetails = !string.IsNullOrWhiteSpace(record.Details);
            return task;
        });
        foreach (var root in TaskTree.Build(items)) Attach(root, false);
        var active = AllTasks.ToDictionary(t => t.Id);
        foreach (var pair in _detailsWindows.ToList())
        {
            if (!active.ContainsKey(pair.Key))
            {
                pair.Value.IsSyncing = false;
                pair.Value.DiscardAndClose();
                _detailsSessions.Remove(pair.Key);
            }
        }
        foreach (var pair in _detailsSessions.ToList())
        {
            if (!active.TryGetValue(pair.Key, out var task)) { pair.Value.Discard(); _detailsSessions.Remove(pair.Key); continue; }
            var text = snapshot.Tasks.Single(t => t.Id == pair.Key).Details;
            pair.Value.Replace(text);
            if (_detailsWindows.TryGetValue(pair.Key, out var note)) note.RefreshTask(task, text);
        }
        RefreshCompletionLocks();
    }

    void OpenDetails(TaskItem task)
    {
        if (_detailsWindows.TryGetValue(task.Id, out var existing))
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            if (task.HasSubtasks) existing.ShowSubtasks();
            existing.Activate();
            return;
        }
        try
        {
            Save(); // Persist identity before creating any details file.
            if (!_detailsSessions.TryGetValue(task.Id, out var session))
            {
                var id = task.Id;
                session = new DetailsSaveSession(Storage.LoadDetails(id), text =>
                    Storage.SaveTaskDetails(AllTasks.Single(t => t.Id == id), text, _tasks))
                {
                    AutoSaveIntervalSeconds = _settings.DetailsAutoSaveIntervalSeconds
                };
                _detailsSessions.Add(task.Id, session);
            }
            var note = new DetailsWindow(task, session, this) { Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            note.ApplyAppearance(Background, HeaderBar.Background, Foreground, Opacity, _zoom);
            if (task.HasSubtasks) note.ShowSubtasks();
            _detailsWindows.Add(task.Id, note);
            note.SetCompletionState(task.IsBlocked, task.IsDone);
            note.Closed += (_, _) => _detailsWindows.Remove(task.Id);
            note.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not open task details.\n\n{ex.Message}", "Details unavailable", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null)
        {
            if (d is T t) return t;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return null;
    }

    void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<Button>(e.OriginalSource as DependencyObject) != null) return;
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    void Close_Click(object sender, RoutedEventArgs e) => Close();

    void ApplyAppearance(AppearanceSettings settings)
    {
        _settings = settings;
        Color color;
        try { color = (Color)ColorConverter.ConvertFromString(settings.BackgroundColor); }
        catch { color = Color.FromRgb(249, 237, 181); }
        color.A = 255;
        settings.BackgroundColor = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        settings.Opacity = double.IsFinite(settings.Opacity) ? Math.Clamp(settings.Opacity, 0.2, 1) : 1;
        Background = new SolidColorBrush(color);
        HeaderBar.Background = new SolidColorBrush(Color.FromRgb(
            (byte)(color.R * 0.95), (byte)(color.G * 0.95), (byte)(color.B * 0.95)));
        var foreground = new SolidColorBrush(
            color.R * 0.299 + color.G * 0.587 + color.B * 0.114 < 140 ? Colors.White : Color.FromRgb(53, 50, 37));
        Foreground = foreground;
        Resources["TaskForeground"] = foreground;
        Resources["AppBackground"] = Background;
        Resources["HighPriorityFontWeight"] = settings.BoldHighPriority ? FontWeights.Bold : FontWeights.Normal;
        TaskView.ApplyAppearance(Background, Foreground, settings.BoldHighPriority);
        Opacity = settings.Opacity;
        ApplyZoom(settings.ZoomPercent / 100.0);
        foreach (var note in _detailsWindows.Values)
            note.ApplyAppearance(Background, HeaderBar.Background, Foreground, Opacity, _zoom);
        foreach (var pair in _detailsSessions.ToList())
        {
            if (pair.Value.AutoSaveIntervalSeconds == settings.DetailsAutoSaveIntervalSeconds) continue;
            pair.Value.AutoSaveIntervalSeconds = settings.DetailsAutoSaveIntervalSeconds;
            if (_detailsWindows.TryGetValue(pair.Key, out var note)) note.RescheduleAutoSave();
        }
        UpdateListHeight();
    }

    void ApplyZoom(double zoom)
    {
        if (double.IsNaN(_unzoomedWidth)) _unzoomedWidth = (double.IsNaN(Width) ? ActualWidth : Width) / _zoom;
        if (zoom == _zoom && !double.IsNaN(_appliedWidth)) return;
        _zoom = zoom;
        Zoom.Apply(RootDock, zoom);
        // While SizeToContent is active a shown window ignores Width changes, and a transform change alone does not
        // refit the height. Resize manually, then re-arm SizeToContent. MinWidth changes resize the window
        // synchronously, so those intermediate sizes must not be recorded as user resizes.
        _applyingZoom = true;
        try
        {
            SizeToContent = SizeToContent.Manual;
            double available = SystemParameters.WorkArea.Width;
            MinWidth = Zoom.Fit(BaseMinWidth, zoom, available);
            Width = _appliedWidth = Math.Max(MinWidth, Zoom.Fit(_unzoomedWidth, zoom, available));
            SizeToContent = SizeToContent.Height;
        }
        finally { _applyingZoom = false; }
    }

    void Settings_Click(object sender, RoutedEventArgs e)
    {
        CommitAllEdits();
        var originalSettings = _settings;
        var dialog = new SettingsWindow(_settings, ApplyAppearance, _google) { Owner = this };
        _settings = dialog.ShowDialog() == true ? dialog.Settings : originalSettings;
        ApplyAppearance(_settings);
    }

    void CommitAllEdits()
    {
        TaskView.CommitAllEdits();
        foreach (var note in _detailsWindows.Values) note.CommitTaskEdits();
    }
    void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e) => TaskView.Window_PreviewMouseDown(sender, e);
    void Window_PreviewTextInput(object sender, TextCompositionEventArgs e) => TaskView.Window_PreviewTextInput(sender, e);
    void Window_Deactivated(object? sender, EventArgs e) => CommitAllEdits();
    void AddToggle_Click(object sender, RoutedEventArgs e) => TaskView.ToggleAddPanel();
}
