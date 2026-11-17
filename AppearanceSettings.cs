using System.Windows;
using System.Windows.Media;

namespace StickyTodo;

public class AppearanceSettings
{
    int _itemDeleteRetentionSeconds = 5;
    int _maxItemsToDisplay = 10;
    int _detailsAutoSaveIntervalSeconds = 60;
    int _zoomPercent = 100;

    public string BackgroundColor { get; set; } = "#F9EDB5";
    public double Opacity { get; set; } = 1;
    public int ItemDeleteRetentionSeconds
    {
        get => _itemDeleteRetentionSeconds;
        set => _itemDeleteRetentionSeconds = Math.Max(0, value);
    }
    public int MaxItemsToDisplay
    {
        get => _maxItemsToDisplay;
        set => _maxItemsToDisplay = Math.Clamp(value, 1, 100);
    }
    public bool BoldHighPriority { get; set; }
    public int DetailsAutoSaveIntervalSeconds
    {
        get => _detailsAutoSaveIntervalSeconds;
        set => _detailsAutoSaveIntervalSeconds = Math.Clamp(value, 0, 3600);
    }
    public int ZoomPercent
    {
        get => _zoomPercent;
        set => _zoomPercent = Math.Clamp(value, 50, 150);
    }
}

public static class Zoom
{
    // Scales the whole content tree, including fonts and popups placed inside it.
    public static void Apply(FrameworkElement content, double zoom) =>
        content.LayoutTransform = zoom == 1 ? Transform.Identity : new ScaleTransform(zoom, zoom);

    // Scales a window dimension while keeping it on the work area.
    public static double Fit(double baseSize, double zoom, double available) => Math.Min(baseSize * zoom, available);
}
