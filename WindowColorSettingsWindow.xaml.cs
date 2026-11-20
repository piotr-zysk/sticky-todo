using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace StickyTodo;

public partial class WindowColorSettingsWindow : Window
{
    readonly Action<string?> _preview;
    readonly Action<string?> _save;
    readonly string _inheritedColor;
    string? _color;
    bool _updating;

    public WindowColorSettingsWindow(string? color, string inheritedColor, Action<string?> preview, Action<string?> save)
    {
        _color = ColorHelper.Normalize(color);
        _inheritedColor = inheritedColor;
        _preview = preview;
        _save = save;
        InitializeComponent();
        SetControls();
    }

    void SetControls()
    {
        _updating = true;
        var value = _color ?? _inheritedColor;
        var color = (Color)ColorConverter.ConvertFromString(value);
        HexColor.Text = value;
        RedSlider.Value = color.R;
        GreenSlider.Value = color.G;
        BlueSlider.Value = color.B;
        ColorPreview.Background = new SolidColorBrush(color);
        InheritanceStatus.Text = _color == null ? "Inherited from parent or global settings" : "Explicit color for this window";
        ColorError.Visibility = Visibility.Collapsed;
        SaveButton.IsEnabled = true;
        _updating = false;
    }

    void Color_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updating || !IsInitialized) return;
        _color = $"#{(byte)RedSlider.Value:X2}{(byte)GreenSlider.Value:X2}{(byte)BlueSlider.Value:X2}";
        SetControls();
        _preview(_color);
    }

    void Hex_Changed(object sender, TextChangedEventArgs e)
    {
        if (_updating || !IsInitialized) return;
        var color = ColorHelper.Normalize(HexColor.Text);
        SaveButton.IsEnabled = color != null;
        ColorError.Visibility = color == null ? Visibility.Visible : Visibility.Collapsed;
        if (color == null) return;
        _color = color;
        SetControls();
        _preview(_color);
    }

    void Clear_Click(object sender, RoutedEventArgs e)
    {
        _color = null;
        SetControls();
        _preview(null);
    }

    void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!SaveButton.IsEnabled) return;
        try { _save(_color); }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not save window color: {ex.Message}", "Settings", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        DialogResult = true;
    }
}
