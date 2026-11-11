using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace StickyTodo;

public partial class TaskListControl : UserControl
{
    ObservableCollection<TaskItem> _tasks = new();
    MainWindow? _coordinator;
    Guid? _parentId;
    Point _dragStart;
    TaskItem? _dragItem, _markedItem;

    public TaskListControl()
    {
        InitializeComponent();
        AddPanel.SizeChanged += (_, _) => UpdateListHeight();
    }

    public void Configure(MainWindow coordinator, ObservableCollection<TaskItem> tasks, Guid? parentId = null)
    {
        Detach();
        _coordinator = coordinator;
        _tasks = tasks;
        _parentId = parentId;
        TaskList.ItemsSource = tasks;
        tasks.CollectionChanged += OnCollectionChanged;
        UpdateListHeight();
    }

    public void Detach() => _tasks.CollectionChanged -= OnCollectionChanged;
    public bool HasActiveEditor => AddPanel.Visibility == Visibility.Visible || _tasks.Any(t => t.IsEditingTitle || t.IsEditingDue);
    void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateListHeight();
    public void UpdateListHeight()
    {
        var max = _coordinator?.Settings.MaxItemsToDisplay ?? 10;
        var zoom = (_coordinator?.Settings.ZoomPercent ?? 100) / 100.0;
        var availableRows = Math.Max(1, (int)((SystemParameters.WorkArea.Height / zoom - 34 -
            (AddPanel.Visibility == Visibility.Visible ? AddPanel.ActualHeight : 0)) / 24));
        max = Math.Min(max, availableRows);
        TaskList.Height = 24 * Math.Clamp(_tasks.Count, 1, max);
        ScrollViewer.SetVerticalScrollBarVisibility(TaskList,
            _tasks.Count > max ? ScrollBarVisibility.Auto : ScrollBarVisibility.Hidden);
    }

    public void ApplyAppearance(Brush background, Brush foreground, bool bold)
    {
        Resources["AppBackground"] = background;
        Resources["TaskForeground"] = foreground;
        Resources["HighPriorityFontWeight"] = bold ? FontWeights.Bold : FontWeights.Normal;
        UpdateListHeight();
    }

    void Save() => _coordinator!.SaveAll();
    public void ToggleAddPanel() => AddToggle_Click(this, new RoutedEventArgs());
    void Details_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (ItemOf(sender) is { } task) _coordinator!.ShowDetails(task);
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

    public void CommitAllEdits()
    {
        foreach (var t in _tasks.ToList())
        {
            CommitTitle(t);
            t.IsEditingDue = false;
        }
    }

    public void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<TextBox>(e.OriginalSource as DependencyObject) != null) return;
        foreach (var t in _tasks.ToList()) CommitTitle(t);
    }

    public void Window_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox || FindAncestor<TextBox>(e.OriginalSource as DependencyObject) != null) return;
        if (string.IsNullOrEmpty(e.Text) || char.IsControl(e.Text[0])) return;
        AddPanel.Visibility = Visibility.Visible;
        NewTitle.Focus();
        NewTitle.Text = e.Text;
        NewTitle.CaretIndex = NewTitle.Text.Length;
        e.Handled = true;
    }

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
        if (top && !date.HasValue) date = DateTime.Today;
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
        var t = new TaskItem { ParentId = _parentId, Title = title, IsHigh = NewHigh.IsChecked == true };
        t.SetDue(date, time);
        _coordinator!.AddTask(t, top);
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
        if (e.Data.GetData(typeof(TaskItem)) is not TaskItem incoming || !_tasks.Contains(incoming)) { e.Effects = DragDropEffects.None; e.Handled = true; return; }
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
        if (e.Data.GetData(typeof(TaskItem)) is not TaskItem dragged || !_tasks.Contains(dragged)) return;
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

