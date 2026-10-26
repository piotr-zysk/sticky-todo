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
        Settings = new() { BackgroundColor = settings.BackgroundColor, Opacity = settings.Opacity };
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
        ColorError.Visibility = Visibility.Collapsed;
        SaveButton.IsEnabled = true;
        _updating = false;
    }

    void Color_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updating || !IsInitialized) return;
        Settings.BackgroundColor = $"#{(byte)RedSlider.Value:X2}{(byte)GreenSlider.Value:X2}{(byte)BlueSlider.Value:X2}";
        SetControls();
        _preview(Settings);
    }

    void Hex_Changed(object sender, TextChangedEventArgs e)
    {
        if (_updating || !IsInitialized) return;
        var value = HexColor.Text;
        bool valid = value.Length == 7 && value[0] == '#' &&
            uint.TryParse(value.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _);
        ColorError.Visibility = valid ? Visibility.Collapsed : Visibility.Visible;
        SaveButton.IsEnabled = valid;
        if (!valid) return;
        Settings.BackgroundColor = value.ToUpperInvariant();
        SetControls();
        _preview(Settings);
    }

    void Opacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updating || !IsInitialized) return;
        Settings.Opacity = OpacitySlider.Value / 100;
        _preview(Settings);
    }

    void DefaultColor_Click(object sender, RoutedEventArgs e)
    {
        Settings.BackgroundColor = new AppearanceSettings().BackgroundColor;
        SetControls();
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
        try { Storage.SaveSettings(Settings); }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not save settings: {ex.Message}", "Settings", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        DialogResult = true;
    }
}
