using System.Windows;

namespace StickyTodo;

public enum SyncFailureChoice { Cancel, Retry, CloseAnyway }

public partial class SyncFailureWindow : Window
{
    public SyncFailureChoice Choice { get; private set; }
    public SyncFailureWindow(string error)
    {
        InitializeComponent();
        ErrorText.Text = error;
    }
    void Retry_Click(object sender, RoutedEventArgs e) { Choice = SyncFailureChoice.Retry; DialogResult = true; }
    void CloseAnyway_Click(object sender, RoutedEventArgs e) { Choice = SyncFailureChoice.CloseAnyway; DialogResult = true; }
}
