using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Colors = Microsoft.UI.Colors;

namespace PyWinUI.Host.Rendering;

/// <summary>
/// 调色框：不再用 WinUI 原生 ColorPicker，也不再是内嵌的 RGB 滑块面板。
///
/// 形态改为「当前颜色色块 + 点击弹出独立的『编辑颜色』窗口」：
/// 色块本身就是入口，点它打开 <see cref="ColorDialogWindow"/>（6×8 基本色板、
/// 8×2 自定义色、色相/亮度区、HSV/RGB 数值框）。用户点「确定」才把颜色写回。
///
/// 对外契约与原生控件一致（<see cref="Color"/> 属性 + <see cref="ColorChanged"/>
/// 事件），所以 ExtendedProperties / ExtendedEvents / QueryExecutor 的接线不变。
/// </summary>
internal sealed class CustomColorPicker : UserControl
{
    private readonly Border _swatch;
    private readonly TextBlock _label;
    private Color _color = Colors.White;
    private ColorDialogWindow? _dialog;

    public CustomColorPicker()
    {
        _swatch = new Border
        {
            Width = 64,
            Height = 32,
            CornerRadius = new CornerRadius(3),
            BorderBrush = new SolidColorBrush(Colors.Gray),
            BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(_color),
        };

        _label = new TextBlock
        {
            Text = "#FFFFFFFF",
            VerticalAlignment = VerticalAlignment.Center,
            FontFamily = new FontFamily("Consolas"),
            Margin = new Thickness(8, 0, 0, 0),
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(_swatch);
        row.Children.Add(_label);

        Content = row;
        // 整个控件可点，点哪里都弹窗，扩大命中区域
        IsTabStop = true;
        PointerPressed += OnSwatchPressed;
        KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter
                || e.Key == Windows.System.VirtualKey.Space)
            {
                OpenDialog();
            }
        };
    }

    /// <summary>颜色变化事件，参数就是新的 Color 值。</summary>
    public delegate void ColorChangedHandler(Color color);

    public event ColorChangedHandler? ColorChanged;

    public Color Color
    {
        get => _color;
        set
        {
            if (_color.A == value.A && _color.R == value.R
                && _color.G == value.G && _color.B == value.B)
            {
                return;
            }

            _color = value;
            RefreshVisual();
        }
    }

    private void OnSwatchPressed(object sender, PointerRoutedEventArgs e) => OpenDialog();

    private void OpenDialog()
    {
        // 同一时刻只开一个编辑窗
        if (_dialog is not null)
        {
            return;
        }

        var dialog = new ColorDialogWindow(_color, ApplyPickedColor);
        // 无论确定、取消还是点 ×，窗口关闭后都要清掉引用，
        // 否则之后再点色块会因为 _dialog 非空而打不开。
        dialog.Closed += () => _dialog = null;
        _dialog = dialog;
        dialog.Show();
    }

    private void ApplyPickedColor(Color color)
    {
        _dialog = null;
        if (_color.A == color.A && _color.R == color.R
            && _color.G == color.G && _color.B == color.B)
        {
            return;
        }

        _color = color;
        RefreshVisual();
        ColorChanged?.Invoke(color);
    }

    private void RefreshVisual()
    {
        _swatch.Background = new SolidColorBrush(_color);
        _label.Text = $"#{_color.A:X2}{_color.R:X2}{_color.G:X2}{_color.B:X2}";
    }
}
