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
    public List<string> RecentColors { get; set; } = new();
    Dictionary<string, string> _windowBackgroundColors = new();
    public Dictionary<string, string> WindowBackgroundColors
    {
        get => _windowBackgroundColors;
        set
        {
            _windowBackgroundColors = new();
            if (value == null) return;
            foreach (var pair in value)
                if (Guid.TryParse(pair.Key, out var id) && id != Guid.Empty && ColorHelper.Normalize(pair.Value) is { } color)
                    _windowBackgroundColors[id.ToString("N")] = color;
        }
    }

    public AppearanceSettings Copy()
    {
        var copy = (AppearanceSettings)MemberwiseClone();
        copy.RecentColors = ColorHelper.Sanitize(RecentColors);
        copy.WindowBackgroundColors = WindowBackgroundColors;
        return copy;
    }
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

public static class ColorHelper
{
    public const int MaxRecent = 4;

    public static Color ForegroundFor(Color color) =>
        color.R * 0.299 + color.G * 0.587 + color.B * 0.114 < 140 ? Colors.White : Color.FromRgb(53, 50, 37);

    public static string? Normalize(string? value)
    {
        if (value is not { Length: 7 } || value[0] != '#' ||
            !uint.TryParse(value.AsSpan(1), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out _))
            return null;
        return value.ToUpperInvariant();
    }

    public static List<string> Sanitize(IEnumerable<string>? colors) =>
        (colors ?? []).Select(Normalize).OfType<string>().Distinct().Take(MaxRecent).ToList();

    public static List<string> Remember(IEnumerable<string>? colors, string color) =>
        Sanitize((Normalize(color) is { } c ? new[] { c } : Array.Empty<string>()).Concat(colors ?? []));
}

public static class Zoom
{
    // Scales the whole content tree, including fonts and popups placed inside it.
    public static void Apply(FrameworkElement content, double zoom) =>
        content.LayoutTransform = zoom == 1 ? Transform.Identity : new ScaleTransform(zoom, zoom);

    // Scales a window dimension while keeping it on the work area.
    public static double Fit(double baseSize, double zoom, double available) => Math.Min(baseSize * zoom, available);
}
