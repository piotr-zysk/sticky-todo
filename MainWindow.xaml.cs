using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
    Point _dragStart;
    TaskItem? _dragItem;
    TaskItem? _markedItem;
    const double ItemHeight = 24;
    readonly GoogleConnection? _google;
    readonly Func<IDriveTransport>? _driveTransportFactory;
    bool _syncInProgress, _closeInProgress, _allowClose;

    public MainWindow(bool enableGoogle = true, GoogleConnection? google = null, Func<IDriveTransport>? driveTransportFactory = null)
    {
        InitializeComponent();
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
        TaskList.ItemsSource = _tasks;
        _tasks.CollectionChanged += (_, _) =>
        {
            UpdateListHeight();
            Dispatcher.BeginInvoke(UpdateListHeight, DispatcherPriority.Loaded);
        };
        _overdueTimer.Tick += (_, _) => { foreach (var t in _tasks) t.RefreshOverdue(); };
        _overdueTimer.Start();
        Closing += OnClosing;
        UpdateListHeight();
    }

    void UpdateListHeight()
    {
        int max = _settings.MaxItemsToDisplay;
        TaskList.Height = ItemHeight * Math.Clamp(_tasks.Count, 1, max);
        ScrollViewer.SetVerticalScrollBarVisibility(TaskList,
            _tasks.Count > max ? ScrollBarVisibility.Auto : ScrollBarVisibility.Hidden);
    }

    // Resizing by the user makes WPF drop SizeToContent; restore it so the height keeps following the items.
    void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
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
        t.PropertyChanged += OnItemChanged;
        if (top) _tasks.Insert(0, t); else _tasks.Add(t);
    }

    void Save() => Storage.SaveTasks(_tasks);

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
                    timer.Tick += (_, _) => { timer.Stop(); t.RemoveTimer = null; Complete(t); };
                    t.RemoveTimer = timer;
                    timer.Start();
                }
                else
                {
                    t.RemoveTimer?.Stop();
                    t.RemoveTimer = null;
                }
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
        if (!_tasks.Contains(t)) return;
        try
        {
            Storage.CompleteTask(t, _tasks.Where(item => item != t));
            if (_detailsWindows.TryGetValue(t.Id, out var note)) note.DiscardAndClose();
            _detailsSessions.Remove(t.Id);
            _tasks.Remove(t);
            t.PropertyChanged -= OnItemChanged;
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
        foreach (var t in _tasks.Where(x => x.IsDone).ToList())
        {
            t.RemoveTimer?.Stop();
            Complete(t);
            if (_tasks.Contains(t)) throw new System.IO.IOException("Could not archive a completed task.");
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
                        var dialog = new SyncFailureWindow(FriendlySyncError(ex)) { Owner = this };
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
        var timers = _tasks.Where(t => t.RemoveTimer?.IsEnabled == true).Select(t => t.RemoveTimer!).ToList();
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
            foreach (var timer in timers) timer.Start();
            RootDock.IsEnabled = true;
            _syncInProgress = false;
            UpdateSyncStatus();
        }
    }

    void ApplySyncedTasks(SyncSnapshot snapshot)
    {
        foreach (var task in _tasks) task.PropertyChanged -= OnItemChanged;
        _tasks.Clear();
        foreach (var record in snapshot.Tasks.Where(t => !t.CompletedAt.HasValue)) Attach(Storage.ToTask(record), false);
        var active = _tasks.ToDictionary(t => t.Id);
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
    }

    void Details_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (ItemOf(sender) is not { } task) return;
        OpenDetails(task);
    }

    void OpenDetails(TaskItem task)
    {
        if (_detailsWindows.TryGetValue(task.Id, out var existing))
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
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
                    Storage.SaveTaskDetails(_tasks.Single(t => t.Id == id), text, _tasks));
                _detailsSessions.Add(task.Id, session);
            }
            var note = new DetailsWindow(task, session) { Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            note.ApplyAppearance(Background, HeaderBar.Background, Foreground, Opacity);
            _detailsWindows.Add(task.Id, note);
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

    static TaskItem? ItemOf(object sender) => (sender as FrameworkElement)?.DataContext as TaskItem;

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
        Resources["HighPriorityFontWeight"] = settings.BoldHighPriority ? FontWeights.Bold : FontWeights.Normal;
        Opacity = settings.Opacity;
        foreach (var note in _detailsWindows.Values)
            note.ApplyAppearance(Background, HeaderBar.Background, Foreground, Opacity);
        UpdateListHeight();
    }

    void Settings_Click(object sender, RoutedEventArgs e)
    {
        CommitAllEdits();
        var originalSettings = _settings;
        var dialog = new SettingsWindow(_settings, ApplyAppearance, _google) { Owner = this };
        _settings = dialog.ShowDialog() == true ? dialog.Settings : originalSettings;
        ApplyAppearance(_settings);
    }

    void Priority_Click(object sender, MouseButtonEventArgs e)
    {
        if (ItemOf(sender) is { } t) t.IsHigh = !t.IsHigh;
    }

    void Title_Click(object sender, MouseButtonEventArgs e)
    {
        if (ItemOf(sender) is { } t && !t.IsDone)
        {
            t.EditTitle = t.Title;
            t.IsEditingTitle = true;
        }
    }

    void TitleBox_VisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is TextBox tb && tb.IsVisible)
            Dispatcher.BeginInvoke(() => { tb.Focus(); tb.SelectAll(); }, DispatcherPriority.Input);
    }

    void TitleBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox tb || tb.DataContext is not TaskItem t) return;
        if (e.Key == Key.Enter) { CommitTitle(t); e.Handled = true; }
        else if (e.Key == Key.Escape) { t.IsEditingTitle = false; e.Handled = true; }
    }

    void TitleBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb && tb.DataContext is TaskItem t && t.IsEditingTitle) CommitTitle(t);
    }

    static void CommitTitle(TaskItem t)
    {
        if (!t.IsEditingTitle) return;
        var text = t.EditTitle.Trim();
        t.IsEditingTitle = false;
        if (text.Length > 0 && text != t.Title) t.Title = text;
    }

    void CommitAllEdits()
    {
        foreach (var t in _tasks.ToList())
        {
            CommitTitle(t);
            t.IsEditingDue = false;
        }
    }

    void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<TextBox>(e.OriginalSource as DependencyObject) != null) return;
        foreach (var t in _tasks.ToList()) CommitTitle(t);
    }

    void Window_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox || FindAncestor<TextBox>(e.OriginalSource as DependencyObject) != null) return;
        if (string.IsNullOrEmpty(e.Text) || char.IsControl(e.Text[0])) return;
        AddPanel.Visibility = Visibility.Visible;
        NewTitle.Focus();
        NewTitle.Text = e.Text;
        NewTitle.CaretIndex = NewTitle.Text.Length;
        e.Handled = true;
    }

    void Window_Deactivated(object? sender, EventArgs e) => CommitAllEdits();

    void Due_Click(object sender, MouseButtonEventArgs e)
    {
        if (ItemOf(sender) is not { } t || t.IsDone) return;
        t.EditDate = t.DueDate;
        t.EditHour = t.DueTime?.Hours;
        t.EditMinute = t.DueTime?.Minutes;
        t.IsEditingDue = true;
    }

    void DuePopup_Closed(object? sender, EventArgs e)
    {
        if (sender is not Popup { DataContext: TaskItem t }) return;
        t.IsEditingDue = false;
        TimeSpan? time = t.EditHour.HasValue ? new TimeSpan(t.EditHour.Value, t.EditMinute ?? 0, 0) : null;
        if (t.EditDate == t.DueDate && time == t.DueTime) return;
        t.SetDue(t.EditDate, time);
        Save();
    }

    void DueOk_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } t) t.IsEditingDue = false;
    }

    void DueClear_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } t) return;
        t.EditDate = null;
        t.EditHour = null;
        t.EditMinute = null;
        t.IsEditingDue = false;
    }

    void NoTime_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } t) return;
        t.EditHour = null;
        t.EditMinute = null;
    }

    static TaskItem? TagItem(object sender) => (sender as FrameworkElement)?.Tag as TaskItem;

    void Hour_Click(object sender, RoutedEventArgs e)
    {
        if (TagItem(sender) is not { } t || ((Button)sender).Content is not int h) return;
        t.EditHour = h;
        t.EditMinute ??= 0;
        if (!t.EditDate.HasValue)
        {
            var now = DateTime.Now;
            var time = new TimeSpan(h, t.EditMinute.Value, 0);
            t.EditDate = now.Date.AddDays(time < now.TimeOfDay ? 1 : 0);
        }
    }

    void Minute_Click(object sender, RoutedEventArgs e)
    {
        if (TagItem(sender) is not { } t || ((Button)sender).Content is not int m) return;
        if (!t.EditDate.HasValue && !t.EditHour.HasValue)
        {
            var due = DateTime.Now.AddMinutes(m);
            t.EditDate = due.Date;
            t.EditHour = due.Hour;
            t.EditMinute = due.Minute;
            return;
        }
        t.EditHour ??= 9;
        t.EditMinute = m;
    }

    void Calendar_GotMouseCapture(object sender, MouseEventArgs e)
    {
        if (e.OriginalSource is CalendarItem or CalendarDayButton or CalendarButton)
            ((UIElement)e.OriginalSource).ReleaseMouseCapture();
    }

    void AddToggle_Click(object sender, RoutedEventArgs e)
    {
        if (AddPanel.Visibility == Visibility.Visible) { CloseAddPanel(); return; }
        AddPanel.Visibility = Visibility.Visible;
        NewTitle.Focus();
    }

    void CloseAddPanel()
    {
        AddPanel.Visibility = Visibility.Collapsed;
        NewTitle.Text = "";
        NewDate.SelectedDate = null;
        NewTime.Text = "";
        NewHigh.IsChecked = false;
        AddError.Visibility = Visibility.Collapsed;
    }

    void AddCancel_Click(object sender, RoutedEventArgs e) => CloseAddPanel();

    void NewTitle_TextChanged(object sender, TextChangedEventArgs e)
    {
        var ok = !string.IsNullOrWhiteSpace(NewTitle.Text);
        AddTopBtn.IsEnabled = ok;
        AddBottomBtn.IsEnabled = ok;
    }

    void AddPanel_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && AddBottomBtn.IsEnabled) { AddNew(false); e.Handled = true; }
        else if (e.Key == Key.Escape) { CloseAddPanel(); e.Handled = true; }
    }

    void AddTop_Click(object sender, RoutedEventArgs e) => AddNew(true);
    void AddBottom_Click(object sender, RoutedEventArgs e) => AddNew(false);

    void NewTime_LostFocus(object sender, RoutedEventArgs e)
    {
        if (TaskItem.TryParseTime(NewTime.Text, out var time))
            NewTime.Text = time.ToString(@"hh\:mm");
    }

    void AddNew(bool top)
    {
        var title = NewTitle.Text.Trim();
        if (title.Length == 0) return;
        var date = NewDate.SelectedDate;
        TimeSpan? time = null;
        if (!string.IsNullOrWhiteSpace(NewTime.Text))
        {
            if (!TaskItem.TryParseTime(NewTime.Text, out var ts))
            {
                AddError.Text = "Enter a valid time, such as 19:00, 1900, 715, or 7.";
                AddError.Visibility = Visibility.Visible;
                return;
            }
            time = ts;
            if (!date.HasValue)
            {
                var now = DateTime.Now;
                date = now.Date.AddDays(ts < now.TimeOfDay ? 1 : 0);
            }
        }
        var t = new TaskItem { Title = title, IsHigh = NewHigh.IsChecked == true };
        t.SetDue(date, time);
        Attach(t, top);
        Save();
        CloseAddPanel();
        TaskList.ScrollIntoView(t);
    }

    void List_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragItem = null;
        var src = e.OriginalSource as DependencyObject;
        if (FindAncestor<TextBox>(src) != null || FindAncestor<Button>(src) != null || FindAncestor<CheckBox>(src) != null || FindAncestor<ScrollBar>(src) != null) return;
        var lbi = FindAncestor<ListBoxItem>(src);
        if (lbi?.DataContext is TaskItem t && !t.IsDone)
        {
            _dragItem = t;
            _dragStart = e.GetPosition(null);
        }
    }

    void List_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragItem == null || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(null);
        if (Math.Abs(p.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(p.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var item = _dragItem;
        _dragItem = null;
        DragDrop.DoDragDrop(TaskList, new DataObject(typeof(TaskItem), item), DragDropEffects.Move);
        ClearMarker();
    }

    void ClearMarker()
    {
        if (_markedItem != null) { _markedItem.DropMarker = 0; _markedItem = null; }
    }

    (TaskItem? target, bool below) Locate(DragEventArgs e)
    {
        var hit = e.OriginalSource as DependencyObject;
        var lbi = FindAncestor<ListBoxItem>(hit);
        if (lbi?.DataContext is not TaskItem t) return (null, false);
        var y = e.GetPosition(lbi).Y;
        return (t, y > lbi.ActualHeight / 2);
    }

    void List_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(TaskItem))) { e.Effects = DragDropEffects.None; e.Handled = true; return; }
        e.Effects = DragDropEffects.Move;
        var (target, below) = Locate(e);
        ClearMarker();
        if (target != null)
        {
            target.DropMarker = below ? 1 : -1;
            _markedItem = target;
        }
        var sv = FindScrollViewer(TaskList);
        if (sv != null)
        {
            var y = e.GetPosition(TaskList).Y;
            if (y < 20) sv.ScrollToVerticalOffset(sv.VerticalOffset - 8);
            else if (y > TaskList.ActualHeight - 20) sv.ScrollToVerticalOffset(sv.VerticalOffset + 8);
        }
        e.Handled = true;
    }

    void List_DragLeave(object sender, DragEventArgs e) => ClearMarker();

    void List_Drop(object sender, DragEventArgs e)
    {
        ClearMarker();
        if (e.Data.GetData(typeof(TaskItem)) is not TaskItem dragged) return;
        var (target, below) = Locate(e);
        int oldIdx = _tasks.IndexOf(dragged);
        int newIdx;
        if (target == null) newIdx = _tasks.Count - 1;
        else
        {
            if (target == dragged) return;
            newIdx = _tasks.IndexOf(target) + (below ? 1 : 0);
            if (oldIdx < newIdx) newIdx--;
        }
        if (oldIdx < 0 || newIdx == oldIdx) return;
        _tasks.Move(oldIdx, newIdx);
        Save();
        e.Handled = true;
    }

    static ScrollViewer? FindScrollViewer(DependencyObject d)
    {
        if (d is ScrollViewer sv) return sv;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
        {
            var r = FindScrollViewer(VisualTreeHelper.GetChild(d, i));
            if (r != null) return r;
        }
        return null;
    }
}
