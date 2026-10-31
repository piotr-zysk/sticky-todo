using System.Windows;

namespace StickyTodo;

public enum SyncFailureChoice { Cancel, Retry, CloseAnyway }

public partial class SyncFailureWindow : Window
{
    public SyncFailureChoice Choice { get; private set; }
    public SyncFailureWindow(string error, double zoom = 1)
    {
        InitializeComponent();
        ErrorText.Text = error;
        Zoom.Apply(RootPanel, zoom);
        Width = Zoom.Fit(Width, zoom, SystemParameters.WorkArea.Width);
    }
    void Retry_Click(object sender, RoutedEventArgs e) { Choice = SyncFailureChoice.Retry; DialogResult = true; }
    void CloseAnyway_Click(object sender, RoutedEventArgs e) { Choice = SyncFailureChoice.CloseAnyway; DialogResult = true; }
}
