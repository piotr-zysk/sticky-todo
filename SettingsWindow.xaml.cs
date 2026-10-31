using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace StickyTodo;

public partial class SettingsWindow : Window
{
    readonly Action<AppearanceSettings> _preview;
    bool _updating;
    public AppearanceSettings Settings { get; private set; }

    public SettingsWindow(AppearanceSettings settings, Action<AppearanceSettings> preview)
    {
        Settings = new()
        {
            BackgroundColor = settings.BackgroundColor,
            Opacity = settings.Opacity,
            ItemDeleteRetentionSeconds = settings.ItemDeleteRetentionSeconds,
            MaxItemsToDisplay = settings.MaxItemsToDisplay,
            BoldHighPriority = settings.BoldHighPriority
        };
        _preview = preview;
        InitializeComponent();
        SetControls();
    }

    void SetControls()
    {
        _updating = true;
        var color = (Color)ColorConverter.ConvertFromString(Settings.BackgroundColor);
        RedSlider.Value = color.R;
        GreenSlider.Value = color.G;
        BlueSlider.Value = color.B;
        HexColor.Text = Settings.BackgroundColor;
        ColorPreview.Background = new SolidColorBrush(color);
        OpacitySlider.Value = Settings.Opacity * 100;
        DeleteRetentionSeconds.Text = Settings.ItemDeleteRetentionSeconds.ToString(CultureInfo.InvariantCulture);
        MaxItemsToDisplay.Text = Settings.MaxItemsToDisplay.ToString(CultureInfo.InvariantCulture);
        BoldHighPriority.IsChecked = Settings.BoldHighPriority;
        _updating = false;
        UpdateValidation();
    }

    void Color_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updating || !IsInitialized) return;
        Settings.BackgroundColor = $"#{(byte)RedSlider.Value:X2}{(byte)GreenSlider.Value:X2}{(byte)BlueSlider.Value:X2}";
        SetColorControls();
        UpdateValidation();
        _preview(Settings);
    }

    void Hex_Changed(object sender, TextChangedEventArgs e)
    {
        if (_updating || !IsInitialized) return;
        var value = HexColor.Text;
        bool valid = value.Length == 7 && value[0] == '#' &&
            uint.TryParse(value.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _);
        UpdateValidation();
        if (!valid) return;
        Settings.BackgroundColor = value.ToUpperInvariant();
        SetColorControls();
        _preview(Settings);
    }

    void Opacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updating || !IsInitialized) return;
        Settings.Opacity = OpacitySlider.Value / 100;
        UpdateValidation();
        _preview(Settings);
    }

    void SetColorControls()
    {
        _updating = true;
        var color = (Color)ColorConverter.ConvertFromString(Settings.BackgroundColor);
        RedSlider.Value = color.R;
        GreenSlider.Value = color.G;
        BlueSlider.Value = color.B;
        HexColor.Text = Settings.BackgroundColor;
        ColorPreview.Background = new SolidColorBrush(color);
        _updating = false;
    }

    void NumericSettings_Changed(object sender, TextChangedEventArgs e)
    {
        if (_updating || !IsInitialized) return;
        bool valid = TryReadNumericSettings(out var retention, out var maxItems);
        UpdateValidation();
        if (!valid) return;
        Settings.ItemDeleteRetentionSeconds = retention;
        Settings.MaxItemsToDisplay = maxItems;
        _preview(Settings);
    }

    void BoldHighPriority_Changed(object sender, RoutedEventArgs e)
    {
        if (_updating || !IsInitialized) return;
        Settings.BoldHighPriority = BoldHighPriority.IsChecked == true;
        UpdateValidation();
        _preview(Settings);
    }

    bool TryReadNumericSettings(out int retention, out int maxItems)
    {
        bool validRetention = int.TryParse(DeleteRetentionSeconds.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out retention) && retention >= 0;
        bool validMaxItems = int.TryParse(MaxItemsToDisplay.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out maxItems) && maxItems is >= 1 and <= 100;
        return validRetention && validMaxItems;
    }

    void UpdateValidation()
    {
        bool validColor = HexColor.Text.Length == 7 && HexColor.Text[0] == '#' &&
            uint.TryParse(HexColor.Text.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _);
        bool validRetention = int.TryParse(DeleteRetentionSeconds.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var retention) && retention >= 0;
        bool validMaxItems = int.TryParse(MaxItemsToDisplay.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var maxItems) && maxItems is >= 1 and <= 100;
        ColorError.Visibility = validColor ? Visibility.Collapsed : Visibility.Visible;
        RetentionError.Visibility = validRetention ? Visibility.Collapsed : Visibility.Visible;
        MaxItemsError.Visibility = validMaxItems ? Visibility.Collapsed : Visibility.Visible;
        SaveButton.IsEnabled = validColor && validRetention && validMaxItems;
    }

    void DefaultColor_Click(object sender, RoutedEventArgs e)
    {
        Settings.BackgroundColor = new AppearanceSettings().BackgroundColor;
        SetColorControls();
        UpdateValidation();
        _preview(Settings);
    }

    void Reset_Click(object sender, RoutedEventArgs e)
    {
        Settings = new();
        SetControls();
        _preview(Settings);
    }

    void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!SaveButton.IsEnabled) return;
        try { Storage.SaveSettings(Settings); }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not save settings: {ex.Message}", "Settings", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        DialogResult = true;
    }
}
