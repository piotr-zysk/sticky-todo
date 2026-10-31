namespace StickyTodo;

public class AppearanceSettings
{
    int _itemDeleteRetentionSeconds = 5;
    int _maxItemsToDisplay = 10;

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
}
