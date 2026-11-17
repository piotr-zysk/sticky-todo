using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Threading;

namespace StickyTodo;

public partial class DetailsWindow : Window
{
    readonly DetailsSaveSession _session;
    readonly MainWindow? _coordinator;
    readonly DispatcherTimer _saveTimer = new();
    bool _replacing;
    double _zoom = 1;
    const double BaseMinWidth = 240, BaseMinHeight = 180;
    public bool IsSyncing { get; set; }
    public bool IsListView { get; private set; }
    double _textWidth = 400, _textHeight = 300, _listWidth = 400;

    public DetailsWindow(TaskItem task, DetailsSaveSession session) : this(task, session, null) { }

    public DetailsWindow(TaskItem task, DetailsSaveSession session, MainWindow? coordinator)
    {
        InitializeComponent();
        DataContext = task;
        _coordinator = coordinator;
        if (coordinator != null) ChildView.Configure(coordinator, task.Children, task.Id);
        _session = session;
        Editor.Text = session.Text;
        task.HasDetails = !string.IsNullOrWhiteSpace(session.Text);
        Editor.TextChanged += (_, _) =>
        {
            if (_replacing) return;
            _session.Edit(Editor.Text);
            ((TaskItem)DataContext).HasDetails = !string.IsNullOrWhiteSpace(_session.Text);
            SaveAndSchedule();
        };
        _saveTimer.Tick += (_, _) => SaveAndSchedule();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            if (IsListView && ChildView.HasActiveEditor) return;
            e.Handled = true;
            Close();
        };
        Closing += OnClosing;
        Closed += (_, _) => { _saveTimer.Stop(); ChildView.Detach(); };
        Loaded += (_, _) =>
        {
            if (IsListView) ChildView.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
            else Editor.Focus();
        };
        PreviewMouseDown += (_, e) => { if (IsListView) ChildView.Window_PreviewMouseDown(this, e); };
        PreviewTextInput += (_, e) => { if (IsListView && ChildView.IsEnabled) ChildView.Window_PreviewTextInput(this, e); };
        Deactivated += (_, _) => CommitTaskEdits();
        SizeChanged += (_, _) =>
        {
            if (IsListView && SizeToContent != SizeToContent.Height)
                Dispatcher.BeginInvoke(() => { if (IsListView) SizeToContent = SizeToContent.Height; });
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ((HwndSource)PresentationSource.FromVisual(this)).AddHook((IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            if (!IsListView || msg != 0x0084) return IntPtr.Zero;
            var x = PointFromScreen(new Point((short)((long)lParam & 0xFFFF), 0)).X;
            handled = true;
            return (IntPtr)(x < 5 ? 10 : x > ActualWidth - 5 ? 11 : 1);
        });
    }

    public void CommitTaskEdits() => ChildView.CommitAllEdits();

    public void SetCompletionState(bool blocked, bool pending)
    {
        Editor.IsReadOnly = blocked;
        ChildView.IsEnabled = !blocked && !pending;
        AddChildButton.IsEnabled = !blocked && !pending;
    }

    void AddChild_Click(object sender, RoutedEventArgs e) => ChildView.ToggleAddPanel();

    public void ShowSubtasks()
    {
        if (!IsListView) View_Click(this, new RoutedEventArgs());
    }

    void View_Click(object sender, RoutedEventArgs e)
    {
        if (IsListView)
        {
            CommitTaskEdits();
            _listWidth = Width;
        }
        else { _textWidth = Width; _textHeight = Height; }
        IsListView = !IsListView;
        SizeToContent = SizeToContent.Manual;
        Editor.Visibility = IsListView ? Visibility.Collapsed : Visibility.Visible;
        ChildView.Visibility = IsListView ? Visibility.Visible : Visibility.Collapsed;
        CopyButton.Visibility = IsListView ? Visibility.Collapsed : Visibility.Visible;
        AddChildButton.Visibility = IsListView ? Visibility.Visible : Visibility.Collapsed;
        RootPanel.Margin = IsListView ? new Thickness(0) : new Thickness(5);
        HeaderBar.Height = IsListView ? 32 : 30;
        ViewButton.ToolTip = IsListView ? "Show text details" : "Show subtasks";
        AutomationProperties.SetName(ViewButton, (string)ViewButton.ToolTip);
        ViewIcon.Visibility = IsListView ? Visibility.Collapsed : Visibility.Visible;
        TextViewIcon.Visibility = IsListView ? Visibility.Visible : Visibility.Collapsed;
        MinHeight = IsListView ? 0 : Zoom.Fit(BaseMinHeight, _zoom, SystemParameters.WorkArea.Height);
        MinWidth = Zoom.Fit(IsListView ? 320 : BaseMinWidth, _zoom, SystemParameters.WorkArea.Width);
        Width = Math.Min(SystemParameters.WorkArea.Width, Math.Max(MinWidth, IsListView ? _listWidth : _textWidth));
        if (IsListView) SizeToContent = SizeToContent.Height;
        else Height = Math.Min(SystemParameters.WorkArea.Height, _textHeight);
        if (IsListView) ChildView.MoveFocus(new TraversalRequest(FocusNavigationDirection.First)); else Editor.Focus();
    }

    void SaveAndSchedule()
    {
        _saveTimer.Stop();
        try { _session.Save(); }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not save task details. Your text is still open.\n\n{ex.Message}", "Save failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        if (_session.IsDirty && !_session.IsDiscarded && _session.SaveDelay is { } delay)
        {
            _saveTimer.Interval = delay > TimeSpan.Zero ? delay : TimeSpan.FromSeconds(_session.AutoSaveIntervalSeconds);
            _saveTimer.Start();
        }
    }

    public void RescheduleAutoSave() => SaveAndSchedule();

    public void Flush() => _session.Save(force: true);

    public void RefreshTask(TaskItem task, string text)
    {
        _saveTimer.Stop();
        _session.Replace(text);
        DataContext = task;
        if (_coordinator != null) ChildView.Configure(_coordinator, task.Children, task.Id);
        task.HasDetails = !string.IsNullOrWhiteSpace(text);
        _replacing = true;
        try { Editor.Text = text; }
        finally { _replacing = false; }
    }

    public void DiscardAndClose()
    {
        _session.Discard();
        _saveTimer.Stop();
        Close();
    }

    void OnClosing(object? sender, CancelEventArgs e)
    {
        if (IsSyncing) { e.Cancel = true; return; }
        try { CommitTaskEdits(); Flush(); }
        catch (Exception ex)
        {
            e.Cancel = true;
            MessageBox.Show(this, $"Could not save task details. The note will stay open so you can retry.\n\n{ex.Message}", "Save failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    void Copy_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText($"{((TaskItem)DataContext).Title}\n\n{Editor.Text}", TextDataFormat.UnicodeText); }
        catch (System.Runtime.InteropServices.ExternalException ex)
        {
            MessageBox.Show(this, $"Could not copy task details. Another application may be using the clipboard.\n\n{ex.Message}", "Copy failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    public void ApplyAppearance(Brush background, Brush header, Brush foreground, double opacity, double zoom = 1)
    {
        Background = background;
        HeaderBar.Background = header;
        Foreground = foreground;
        ChildView.ApplyAppearance(background, foreground, _coordinator?.Settings.BoldHighPriority ?? false);
        Opacity = opacity;
        ApplyZoom(zoom);
    }

    void ApplyZoom(double zoom)
    {
        if (zoom == _zoom) return;
        double ratio = zoom / _zoom;
        _zoom = zoom;
        if (IsListView) SizeToContent = SizeToContent.Manual;
        Zoom.Apply(RootPanel, zoom);
        var area = SystemParameters.WorkArea;
        // Read the size before changing minimums: raising them resizes the window immediately.
        double width = Zoom.Fit(Width, ratio, area.Width), height = Zoom.Fit(Height, ratio, area.Height);
        _textWidth = Zoom.Fit(_textWidth, ratio, area.Width);
        _textHeight = Zoom.Fit(_textHeight, ratio, area.Height);
        _listWidth = Zoom.Fit(_listWidth, ratio, area.Width);
        MinWidth = Zoom.Fit(IsListView ? 320 : BaseMinWidth, zoom, area.Width);
        MinHeight = IsListView ? 0 : Zoom.Fit(BaseMinHeight, zoom, area.Height);
        Width = Math.Max(MinWidth, width);
        Height = Math.Max(MinHeight, height);
        if (IsListView) SizeToContent = SizeToContent.Height;
    }

    void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source)
            for (var element = source; element != null; element = VisualTreeHelper.GetParent(element))
                if (element is System.Windows.Controls.Button) return;
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    void Close_Click(object sender, RoutedEventArgs e) { e.Handled = true; Close(); }
}
