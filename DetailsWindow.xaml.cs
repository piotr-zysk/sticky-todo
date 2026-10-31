using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace StickyTodo;

public partial class DetailsWindow : Window
{
    readonly DetailsSaveSession _session;
    readonly DispatcherTimer _saveTimer = new();

    public DetailsWindow(TaskItem task, DetailsSaveSession session)
    {
        InitializeComponent();
        DataContext = task;
        _session = session;
        Editor.Text = session.Text;
        Editor.TextChanged += (_, _) => { _session.Edit(Editor.Text); SaveAndSchedule(); };
        _saveTimer.Tick += (_, _) => SaveAndSchedule();
        Closing += OnClosing;
        Closed += (_, _) => _saveTimer.Stop();
        Loaded += (_, _) => Editor.Focus();
    }

    void SaveAndSchedule()
    {
        _saveTimer.Stop();
        try { _session.Save(); }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not save task details. Your text is still open.\n\n{ex.Message}", "Save failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        if (_session.IsDirty && !_session.IsDiscarded)
        {
            var delay = _session.SaveDelay;
            _saveTimer.Interval = delay > TimeSpan.Zero ? delay : TimeSpan.FromMinutes(1);
            _saveTimer.Start();
        }
    }

    public void Flush() => _session.Save(force: true);

    public void DiscardAndClose()
    {
        _session.Discard();
        _saveTimer.Stop();
        Close();
    }

    void OnClosing(object? sender, CancelEventArgs e)
    {
        try { Flush(); }
        catch (Exception ex)
        {
            e.Cancel = true;
            MessageBox.Show(this, $"Could not save task details. The note will stay open so you can retry.\n\n{ex.Message}", "Save failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    public void ApplyAppearance(Brush background, Brush header, Brush foreground, double opacity)
    {
        Background = background;
        HeaderBar.Background = header;
        Foreground = foreground;
        Opacity = opacity;
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
