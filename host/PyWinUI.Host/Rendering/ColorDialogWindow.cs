using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.Graphics;
using Windows.UI;
using Path = System.IO.Path;

namespace PyWinUI.Host.Rendering;

/// <summary>
/// 独立的「编辑颜色」窗口：复刻经典 Windows 调色对话框布局。
///
/// 为什么是独立 Window 而不是 ContentDialog：
/// - ContentDialog 会被 WinUI 限制为同一时刻只能有一个，且无法固定尺寸；
/// - 独立窗口可以用 OverlappedPresenter 关掉最小化/最大化/缩放，
///   并强制前置获得焦点，行为更接近系统级对话框。
///
/// 不使用任何加载态：窗口内容一次性构建完成后才 Activate，直接呈现最终界面。
/// </summary>
internal sealed class ColorDialogWindow
{
    // 经典色板：6 行 × 8 列，逐格从参考图采样得到
    private static readonly string[] BasicColors =
    {
        "#FF8080", "#FFFF80", "#80FF80", "#00FF80", "#80FFFF", "#0080FF", "#FF80C0", "#FF80FF",
        "#FF0000", "#FFFF00", "#80FF00", "#00FF40", "#00FFFF", "#0080C0", "#8080C0", "#FF00FF",
        "#804040", "#FF8040", "#00FF00", "#008080", "#004080", "#8080FF", "#800040", "#FF0080",
        "#800000", "#FF8000", "#008000", "#008040", "#0000FF", "#0000A0", "#800080", "#8000FF",
        "#400000", "#804000", "#004000", "#004040", "#000080", "#000040", "#400040", "#400080",
        "#000000", "#808000", "#808040", "#808080", "#408080", "#C0C0C0", "#400040", "#FFFFFF",
    };

    private const int CustomSlots = 16; // 2 行 × 8 列
    private const double CellSize = 22;
    private const double CellGap = 2;

    /// <summary>色相面板/竖条的顶边位置。上移后底边 269 与预览块（275）留出间隙。</summary>
    private const double SpectrumTop = 11;

    private readonly Window _window;
    private readonly Action<Color> _onConfirm;
    private Color _color;
    private Color _original;

    private Border _preview = null!;
    private readonly Border[] _customSwatches = new Border[CustomSlots];
    private readonly Color[] _customColors = new Color[CustomSlots];
    private TextBox _hueBox = null!, _satBox = null!, _lumBox = null!, _redBox = null!, _greenBox = null!, _blueBox = null!;
    private Canvas _spectrum = null!;
    private Ellipse _cursor = null!;
    private Border _valueBar = null!;
    private Rectangle _valueKnob = null!;
    private bool _syncing;

    /// <summary>
    /// 当前拖动源："spectrum" / "valuebar"。指针捕获在某些情况下会失效，
    /// 两个控件可能同时收到 PointerMoved；靠这个标记保证只有**按下时所在**的
    /// 控件响应，避免拖竖条时色相面板跟着变。
    /// </summary>
    private string? _dragSource;

    /// <summary>
    /// 权威的 HSL 状态。所有控件都从这三个值派生，而不是每次从 RGB 反算——
    /// 否则亮度拖到 0（纯黑）时 h/s 信息会丢失，再拖回来色调/饱和度就被重置了。
    /// </summary>
    private double _h, _s, _l;

    /// <summary>窗口关闭时触发（确定 / 取消 / 右上角 × 都算）。</summary>
    public event Action? Closed;

    public ColorDialogWindow(Color initial, Action<Color> onConfirm)
    {
        _color = initial;
        _original = initial;
        _onConfirm = onConfirm;

        _window = new Window { Title = "编辑颜色" };
        _window.Closed += (_, _) => Closed?.Invoke();

        // 固定尺寸、无最小化/最大化/缩放，符合系统对话框的形态
        var appWindow = _window.AppWindow;
        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }

        // 先构建内容，再显示——中间不出现任何加载态
        _window.Content = BuildContent();
        LoadCustomColors();
        SetColorFromRgb(_color, includeSpectrumCursor: true);
    }

    /// <summary>显示窗口并强制获取焦点。</summary>
    public void Show()
    {
        // 参考图客户区是 527×402（总高 424 减掉 22px 经典标题栏）。
        // AppWindow.Resize 直接对应物理像素（WinUI 无边框窗口客户区≈整窗），
        // 所以客户区宽度直接给 527——之前额外加 frame 补偿导致窗口宽了 18px，
        // 右侧会多出留白，和用户标注的「数值框距右边 20px」对不上。
        const int contentW = 527;
        const int contentH = 402;
        var scale = GetDpiScale();
        var titleBar = 32.0;  // WinUI 3 默认标题栏（DIP）

        _window.AppWindow.Resize(new SizeInt32(
            (int)Math.Round(contentW * scale),
            (int)Math.Round((contentH + titleBar) * scale)));

        var display = DisplayArea.GetFromWindowId(_window.AppWindow.Id, DisplayAreaFallback.Primary);
        var work = display.WorkArea;
        var size = _window.AppWindow.Size;
        _window.AppWindow.Move(new PointInt32(
            work.X + (work.Width - size.Width) / 2,
            work.Y + (work.Height - size.Height) / 2));

        _window.Activate();
        ForceForeground();
    }

    private double GetDpiScale()
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
            var dpi = GetDpiForWindow(hwnd);
            return dpi == 0 ? 1.0 : dpi / 96.0;
        }
        catch
        {
            return 1.0;
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    private void ForceForeground()
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
            SetForegroundWindow(hwnd);
            SetFocus(hwnd);
        }
        catch
        {
            // 置前失败不影响功能
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr hWnd);

    // ------------------------------------------------------------------
    // 界面构建
    // ------------------------------------------------------------------

    private FrameworkElement BuildContent()
    {
        // 按参考图坐标绝对定位（客户区 527×402，坐标已减去 22px 标题栏）。
        // 用 Canvas 而不是 StackPanel，是为了让每个组件的位置和尺寸
        // 与截图严格对齐——StackPanel 会按内容自动撑开，做不到像素级一致。
        var canvas = new Canvas { Width = 527, Height = 402 };

        // ---- 左：基本颜色 ----
        AddText(canvas, "基本颜色(B):", 8, 16, 12);
        for (var i = 0; i < BasicColors.Length; i++)
        {
            var cell = MakeSwatch(ParseHex(BasicColors[i]), isCustom: false, index: -1);
            Canvas.SetLeft(cell, 14 + (i % 8) * 30);
            Canvas.SetTop(cell, 43 + (i / 8) * 29);
            canvas.Children.Add(cell);
        }

        // ---- 左：自定义颜色 ----
        AddText(canvas, "自定义颜色(C):", 8, 232, 12);
        for (var i = 0; i < CustomSlots; i++)
        {
            var cell = MakeSwatch(Microsoft.UI.Colors.White, isCustom: true, index: i);
            _customSwatches[i] = cell;
            Canvas.SetLeft(cell, 14 + (i % 8) * 30);
            Canvas.SetTop(cell, 255 + (i / 8) * 29);
            canvas.Children.Add(cell);
        }

        var specButton = new Button
        {
            Content = "规定自定义颜色(D) >>",
            IsEnabled = false,
            Width = 231,
            Height = 22,
            FontSize = 12,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(4),
        };
        Canvas.SetLeft(specButton, 14);
        Canvas.SetTop(specButton, 330);
        canvas.Children.Add(specButton);

        // ---- 右：色相 / 亮度区 ----
        BuildSpectrum(canvas);

        // ---- 右：预览块（圆角，宽 69，按标注 69×54）----
        _preview = new Border
        {
            Width = 69,
            Height = 54,
            CornerRadius = new CornerRadius(6),
            BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Gray),
            BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(_color),
        };
        Canvas.SetLeft(_preview, 271);
        Canvas.SetTop(_preview, 275);
        canvas.Children.Add(_preview);

        AddText(canvas, "颜色|纯色(O)", 271, 340, 9);

        // ---- 右：六个数值输入框（红框标注 31×25，行距 4~5px）----
        _hueBox = MakeNumberBox();
        _satBox = MakeNumberBox();
        _lumBox = MakeNumberBox();
        _redBox = MakeNumberBox();
        _greenBox = MakeNumberBox();
        _blueBox = MakeNumberBox();

        // 预览块右缘 340，所以第二行「饱和度(S):」标签从 344 起，避让预览块
        // 标签右对齐到框左缘：左列 399、右列 476
        AddLabeledBox(canvas, "色调(E):", _hueBox, 399, 275);
        AddLabeledBox(canvas, "红(R):", _redBox, 476, 275);
        AddLabeledBox(canvas, "饱和度(S):", _satBox, 399, 304);
        AddLabeledBox(canvas, "绿(G):", _greenBox, 476, 304);
        AddLabeledBox(canvas, "亮度(L):", _lumBox, 399, 334);
        AddLabeledBox(canvas, "蓝(U):", _blueBox, 476, 334);

        _hueBox.TextChanged += (_, _) => ApplyFromNumbers(changed: "h");
        _satBox.TextChanged += (_, _) => ApplyFromNumbers(changed: "s");
        _lumBox.TextChanged += (_, _) => ApplyFromNumbers(changed: "l");
        _redBox.TextChanged += (_, _) => ApplyFromNumbers(changed: "r");
        _greenBox.TextChanged += (_, _) => ApplyFromNumbers(changed: "g");
        _blueBox.TextChanged += (_, _) => ApplyFromNumbers(changed: "b");

        // ---- 右：添加到自定义颜色（红框标注 246×26）----
        var addCustom = new Button
        {
            Content = "添加到自定义颜色(A)",
            Width = 246,
            Height = 26,
            FontSize = 12,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(4),
        };
        addCustom.Click += (_, _) => AddToCustomColors();
        Canvas.SetLeft(addCustom, 272);
        Canvas.SetTop(addCustom, 365);
        canvas.Children.Add(addCustom);

        // ---- 底：确定 / 取消 ----
        var okButton = new Button
        {
            Content = "确定",
            Width = 74,
            Height = 24,
            FontSize = 12,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(4),
        };
        okButton.Click += (_, _) =>
        {
            _onConfirm(_color);
            PersistCustomColors();
            _window.Close();
        };
        Canvas.SetLeft(okButton, 11);
        Canvas.SetTop(okButton, 364);
        canvas.Children.Add(okButton);

        var cancelButton = new Button
        {
            Content = "取消",
            Width = 74,
            Height = 24,
            FontSize = 12,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(4),
        };
        cancelButton.Click += (_, _) =>
        {
            _color = _original;
            _window.Close();
        };
        Canvas.SetLeft(cancelButton, 95);
        Canvas.SetTop(cancelButton, 364);
        canvas.Children.Add(cancelButton);

        return canvas;
    }

    private static void AddText(Canvas canvas, string text, double x, double y, double size)
    {
        var block = new TextBlock { Text = text, FontSize = size };
        Canvas.SetLeft(block, x);
        Canvas.SetTop(block, y);
        canvas.Children.Add(block);
    }

    /// <summary>
    /// 标签 + 数字框。标签字号 12，固定 70px 宽、文字右对齐，右缘贴框左边。
    ///
    /// 不用 Measure(DesiredSize) 自适应宽度：窗口内容在布局前 Measure 出的宽度
    /// 在 DPI 缩放下偏小，会导致长标签仍压到框上。固定 70 足够容纳「饱和度(S):」，
    /// 且右对齐后所有标签右缘统一贴框，视觉整齐。
    /// </summary>
    private static void AddLabeledBox(Canvas canvas, string label, TextBox box, double boxX, double y)
    {
        const double labelWidth = 70;
        var block = new TextBlock
        {
            Text = label,
            FontSize = 12,
            Width = labelWidth,
            TextAlignment = TextAlignment.Right,
        };
        Canvas.SetLeft(block, boxX - labelWidth - 3);
        Canvas.SetTop(block, y + 4);
        canvas.Children.Add(block);

        Canvas.SetLeft(box, boxX);
        Canvas.SetTop(box, y);
        canvas.Children.Add(box);
    }

    private Border MakeSwatch(Color color, bool isCustom, int index)
    {
        var border = new Border
        {
            Width = CellSize,
            Height = CellSize,
            CornerRadius = new CornerRadius(3),
            BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Gray),
            BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(color),
            Tag = index,
        };

        border.PointerPressed += (_, _) =>
        {
            var picked = ((SolidColorBrush)border.Background).Color;
            if (isCustom && index >= 0)
            {
                // 空白自定义格（透明/白）不当作有效色
                if (_customColors[index].A == 0)
                {
                    return;
                }

                picked = _customColors[index];
            }

            SetColorFromRgb(picked, includeSpectrumCursor: true);
        };

        // 白格用淡淡的描边更接近系统观感
        if (color.R >= 250 && color.G >= 250 && color.B >= 250)
        {
            border.BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.DarkGray);
        }

        return border;
    }

    /// <summary>
    /// 构建色相/饱和度面板与右侧亮度竖条。
    ///
    /// 面板用 WriteableBitmap 逐像素生成，而不是叠渐变层——因为正确模型是
    /// HSL 的 H(横) × S(纵)，L 固定 50%：顶部饱和鲜艳、底部是 **50% 灰**，
    /// 不是黑色。渐变层叠不出这种「彩色→灰」的过渡。
    /// </summary>
    private void BuildSpectrum(Canvas canvas)
    {
        const int w = 215;
        const int h = 258;

        // Canvas 默认 Background 为 null，不参与命中测试——必须给透明背景，
        // 否则整个面板点不动（图片子元素还设了 IsHitTestVisible=false 穿透）。
        _spectrum = new Canvas
        {
            Width = w,
            Height = h,
            Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)),
        };

        var image = new Image
        {
            Width = w,
            Height = h,
            Source = BuildSpectrumImage(w, h),
            Stretch = Stretch.Fill,
            IsHitTestVisible = false,
        };

        // 圆角：Border 的 CornerRadius 会把子内容裁成圆角
        var imageBorder = new Border
        {
            Width = w,
            Height = h,
            CornerRadius = new CornerRadius(6),
            Child = image,
            IsHitTestVisible = false,
        };
        _spectrum.Children.Add(imageBorder);

        // 色相/饱和度选中标：圆形
        _cursor = new Ellipse
        {
            Width = 10,
            Height = 10,
            Stroke = new SolidColorBrush(Microsoft.UI.Colors.White),
            StrokeThickness = 2,
            IsHitTestVisible = false,
        };
        // 黑色外圈，保证在浅色区也看得见
        _cursor.Fill = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));
        _spectrum.Children.Add(_cursor);

        // 按下时捕获指针 + 标记拖动源：按住本面板拖到别处（包括另一个面板）时，
        // 事件仍然只发给本面板，另一个面板不会跟着响应。
        _spectrum.PointerPressed += (_, e) =>
        {
            _dragSource = "spectrum";
            _spectrum.CapturePointer(e.Pointer);
            PickFromSpectrum(e.GetCurrentPoint(_spectrum).Position);
        };
        _spectrum.PointerMoved += (_, e) =>
        {
            if (_dragSource == "spectrum" && e.GetCurrentPoint(_spectrum).IsInContact)
            {
                PickFromSpectrum(e.GetCurrentPoint(_spectrum).Position);
            }
        };
        _spectrum.PointerReleased += (_, e) =>
        {
            _dragSource = null;
            _spectrum.ReleasePointerCapture(e.Pointer);
        };
        _spectrum.PointerCaptureLost += (_, _) => _dragSource = null;
        Canvas.SetLeft(_spectrum, 269);
        Canvas.SetTop(_spectrum, SpectrumTop);
        canvas.Children.Add(_spectrum);

        // 右侧亮度竖条：白 → 当前色 → 黑（随色相/饱和度变化）
        _valueBar = new Border
        {
            Width = 14,
            Height = h,
            CornerRadius = new CornerRadius(4),
            BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Gray),
            BorderThickness = new Thickness(1),
        };
        Canvas.SetLeft(_valueBar, 492);
        Canvas.SetTop(_valueBar, SpectrumTop);
        // 竖条也可拖动改亮度：Border 有渐变背景，本身能命中。
        // 同样标记拖动源，避免拖到色相面板时那边跟着变。
        _valueBar.PointerPressed += (_, e) =>
        {
            _dragSource = "valuebar";
            _valueBar.CapturePointer(e.Pointer);
            PickFromValueBar(e.GetCurrentPoint(_valueBar).Position);
        };
        _valueBar.PointerMoved += (_, e) =>
        {
            if (_dragSource == "valuebar" && e.GetCurrentPoint(_valueBar).IsInContact)
            {
                PickFromValueBar(e.GetCurrentPoint(_valueBar).Position);
            }
        };
        _valueBar.PointerReleased += (_, e) =>
        {
            _dragSource = null;
            _valueBar.ReleasePointerCapture(e.Pointer);
        };
        _valueBar.PointerCaptureLost += (_, _) => _dragSource = null;
        canvas.Children.Add(_valueBar);

        // 竖条滑块：1 逻辑像素灰色描边 + 白色填充的圆角矩形
        _valueKnob = new Rectangle
        {
            Width = 18,
            Height = 9,
            RadiusX = 3,
            RadiusY = 3,
            Stroke = new SolidColorBrush(Color.FromArgb(255, 128, 128, 128)),
            StrokeThickness = 1,
            Fill = new SolidColorBrush(Microsoft.UI.Colors.White),
            IsHitTestVisible = false,
        };
        canvas.Children.Add(_valueKnob);
    }

    /// <summary>逐像素生成 H×S（L=50%）色板。</summary>
    private static WriteableBitmap BuildSpectrumImage(int w, int h)
    {
        var bitmap = new WriteableBitmap(w, h);
        var pixels = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        {
            // 顶部 s=1（鲜艳），底部 s=0（50% 灰）
            var s = 1.0 - (double)y / (h - 1);
            for (var x = 0; x < w; x++)
            {
                var hue = (double)x / (w - 1) * 360.0;
                var c = HslToRgb(hue, s, 0.5);
                var i = (y * w + x) * 4;
                pixels[i] = c.B;      // BGRA
                pixels[i + 1] = c.G;
                pixels[i + 2] = c.R;
                pixels[i + 3] = 255;
            }
        }

        using (var stream = bitmap.PixelBuffer.AsStream())
        {
            stream.Write(pixels, 0, pixels.Length);
        }

        bitmap.Invalidate();
        return bitmap;
    }

    /// <summary>竖条渐变随当前颜色更新：白 → 该色 → 黑。
    /// 用权威的 _h/_s 而不是从 _color 反算——亮度为 0 时 _color 是黑，
    /// 反算会得到 s=0，竖条就退化成白-灰-黑了。</summary>
    private void UpdateValueBar()
    {
        var h = _h;
        var s = _s;
        _valueBar.Background = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(0, 1),
            GradientStops =
            {
                new GradientStop { Color = HslToRgb(h, s, 1.0), Offset = 0.0 },
                new GradientStop { Color = HslToRgb(h, s, 0.5), Offset = 0.5 },
                new GradientStop { Color = HslToRgb(h, s, 0.0), Offset = 1.0 },
            },
        };
    }

    private static TextBox MakeNumberBox() => new()
    {
        Width = 31,
        Height = 25,
        Text = "0",
        FontSize = 11,
        Padding = new Thickness(3, 0, 0, 0),
        MinWidth = 0,
        MinHeight = 0,
        CornerRadius = new CornerRadius(3),
        VerticalContentAlignment = VerticalAlignment.Center,
    };

    // ------------------------------------------------------------------
    // 交互
    // ------------------------------------------------------------------

    private void PickFromSpectrum(Windows.Foundation.Point p)
    {
        var x = Math.Clamp(p.X / _spectrum.Width, 0, 1);
        var y = Math.Clamp(p.Y / _spectrum.Height, 0, 1);
        // 只改色相和饱和度，亮度 _l 保持不动
        _h = x * 360.0;
        _s = 1.0 - y;
        SyncFromHsl(includeSpectrumCursor: true);
    }

    /// <summary>点击/拖动亮度竖条：只改 L，色调和饱和度保持不变。</summary>
    private void PickFromValueBar(Windows.Foundation.Point p)
    {
        if (p.Y < 0 || p.Y > _valueBar.Height)
        {
            return;
        }

        _l = Math.Clamp(1.0 - p.Y / _valueBar.Height, 0, 1);
        SyncFromHsl(includeSpectrumCursor: true);
    }

    private void ApplyFromNumbers(string changed)
    {
        if (_syncing)
        {
            return;
        }

        if (changed is "h" or "s" or "l")
        {
            // 程序回写 TextBox 会间接触发 TextChanged。若解析出的值与当前状态
            // 一致（差异在取整误差内），说明只是回写，忽略——否则会把拖动
            // 亮度时的不必要运算也变成状态变更。
            if (changed == "h")
            {
                var v = ReadBox(_hueBox, 0, 360);
                if (Math.Abs(v - _h) < 0.6)
                {
                    return;
                }

                _h = v;
            }
            else if (changed == "s")
            {
                var v = ReadBox(_satBox, 0, 100) / 100.0;
                if (Math.Abs(v - _s) < 0.006)
                {
                    return;
                }

                _s = v;
            }
            else
            {
                var v = ReadBox(_lumBox, 0, 100) / 100.0;
                if (Math.Abs(v - _l) < 0.006)
                {
                    return;
                }

                _l = v;
            }

            SyncFromHsl(includeSpectrumCursor: false);
        }
        else
        {
            var r = (byte)ReadBox(_redBox, 0, 255);
            var g = (byte)ReadBox(_greenBox, 0, 255);
            var b = (byte)ReadBox(_blueBox, 0, 255);

            // 关键保护：拖亮度条时 RGB 框会被回写成新颜色，从而触发这里的
            // TextChanged。此时解析出的 RGB 恰好等于当前颜色，直接忽略，
            // 绝不能反算 HSL——那正是「调亮度时色调/饱和度被改掉」的来源。
            if (r == _color.R && g == _color.G && b == _color.B)
            {
                return;
            }

            SetColorFromRgb(Color.FromArgb(255, r, g, b), includeSpectrumCursor: false);
        }
    }

    private static double ReadBox(TextBox box, double min, double max)
    {
        if (!double.TryParse(box.Text, out var v))
        {
            return min;
        }

        return Math.Clamp(v, min, max);
    }

    private void AddToCustomColors()
    {
        // 从第一个空位开始写；满了就覆盖最后一格
        var slot = Array.IndexOf(_customColors, default(Color));
        if (slot < 0)
        {
            slot = CustomSlots - 1;
            // 右移，保留最近的 15 个
            for (var i = 0; i < CustomSlots - 1; i++)
            {
                _customColors[i] = _customColors[i + 1];
            }
        }

        _customColors[slot] = _color;
        RefreshCustomSwatches();
        PersistCustomColors();
    }

    private void RefreshCustomSwatches()
    {
        for (var i = 0; i < CustomSlots; i++)
        {
            var c = _customColors[i];
            _customSwatches[i].Background = new SolidColorBrush(
                c.A == 0 ? Microsoft.UI.Colors.White : c);
        }
    }

    /// <summary>从 RGB 设定颜色：反算 HSL 后同步 UI（用于初始值、色块点击、RGB 输入）。</summary>
    private void SetColorFromRgb(Color color, bool includeSpectrumCursor)
    {
        var (h, s, l) = RgbToHsl(color);
        _h = h;
        _s = s;
        _l = l;
        SyncFromHsl(includeSpectrumCursor);
    }

    /// <summary>
    /// 由权威 HSL 状态派生所有 UI。各控件只改自己那一维（面板改 h/s、竖条改 l），
    /// 其余维度原样保留，所以调亮度不会影响色调和饱和度。
    /// </summary>
    private void SyncFromHsl(bool includeSpectrumCursor)
    {
        _syncing = true;
        try
        {
            var color = HslToRgb(_h, _s, _l);
            _color = color;
            _preview.Background = new SolidColorBrush(color);

            _hueBox.Text = ((int)Math.Round(_h)).ToString();
            _satBox.Text = ((int)Math.Round(_s * 100)).ToString();
            _lumBox.Text = ((int)Math.Round(_l * 100)).ToString();
            _redBox.Text = color.R.ToString();
            _greenBox.Text = color.G.ToString();
            _blueBox.Text = color.B.ToString();

            if (includeSpectrumCursor)
            {
                // 圆形标：直径 10，定位要减去半径
                Canvas.SetLeft(_cursor, _h / 360.0 * _spectrum.Width - 5);
                Canvas.SetTop(_cursor, (1 - _s) * _spectrum.Height - 5);

                // 竖条滑块：L=1 在顶部，L=0 在底部；滑块高于竖条，所以偏离中心
                UpdateValueBar();
                var knobY = SpectrumTop + (1 - _l) * _valueBar.Height - _valueKnob.Height / 2;
                Canvas.SetLeft(_valueKnob, 492 + (_valueBar.Width - _valueKnob.Width) / 2);
                Canvas.SetTop(_valueKnob, knobY);
                _valueKnob.Visibility = Visibility.Visible;
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    // ------------------------------------------------------------------
    // 自定义颜色持久化（临时目录）
    // ------------------------------------------------------------------

    private static string StorePath => Path.Combine(
        Path.GetTempPath(), "pywinui3-custom-colors.json");

    private void LoadCustomColors()
    {
        try
        {
            if (!File.Exists(StorePath))
            {
                return;
            }

            var text = File.ReadAllText(StorePath);
            var list = JsonSerializer.Deserialize<string[]>(text);
            if (list is null)
            {
                return;
            }

            for (var i = 0; i < Math.Min(list.Length, CustomSlots); i++)
            {
                if (!string.IsNullOrWhiteSpace(list[i]))
                {
                    _customColors[i] = ParseHex(list[i]);
                }
            }

            RefreshCustomSwatches();
        }
        catch
        {
            // 存储损坏不影响主流程
        }
    }

    private void PersistCustomColors()
    {
        try
        {
            var list = new string[CustomSlots];
            for (var i = 0; i < CustomSlots; i++)
            {
                var c = _customColors[i];
                list[i] = c.A == 0 ? "" : $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";
            }

            File.WriteAllText(StorePath, JsonSerializer.Serialize(list));
        }
        catch
        {
            // 写失败只影响下次记忆，不该打断关窗
        }
    }

    // ------------------------------------------------------------------
    // 颜色换算
    // ------------------------------------------------------------------

    private static Color ParseHex(string hex)
    {
        hex = hex.TrimStart('#');
        static byte B(string s, int i) =>
            byte.Parse(s.Substring(i, 2), System.Globalization.NumberStyles.HexNumber);
        return hex.Length switch
        {
            6 => Color.FromArgb(255, B(hex, 0), B(hex, 2), B(hex, 4)),
            8 => Color.FromArgb(B(hex, 0), B(hex, 2), B(hex, 4), B(hex, 6)),
            _ => Microsoft.UI.Colors.White,
        };
    }

    /// <summary>RGB → HSL，H 单位度，S/L 单位 0..1。</summary>
    private static (double h, double s, double l) RgbToHsl(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var l = (max + min) / 2.0;
        double h = 0, s = 0;
        if (Math.Abs(max - min) > 1e-9)
        {
            var d = max - min;
            s = l > 0.5 ? d / (2.0 - max - min) : d / (max + min);
            if (max == r)
            {
                h = (g - b) / d + (g < b ? 6 : 0);
            }
            else if (max == g)
            {
                h = (b - r) / d + 2;
            }
            else
            {
                h = (r - g) / d + 4;
            }

            h *= 60;
        }

        return (h, s, l);
    }

    private static Color HslToRgb(double h, double s, double l)
    {
        h = ((h % 360) + 360) % 360;
        s = Math.Clamp(s, 0, 1);
        l = Math.Clamp(l, 0, 1);
        if (s <= 1e-9)
        {
            var v = (byte)Math.Round(l * 255);
            return Color.FromArgb(255, v, v, v);
        }

        var c = (1 - Math.Abs(2 * l - 1)) * s;
        var x = c * (1 - Math.Abs((h / 60.0 % 2) - 1));
        var m = l - c / 2;
        var sector = (int)(h / 60.0) % 6;
        (double r, double g, double b) = sector switch
        {
            0 => (c, x, 0.0),
            1 => (x, c, 0.0),
            2 => (0.0, c, x),
            3 => (0.0, x, c),
            4 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return Color.FromArgb(
            255,
            (byte)Math.Round((r + m) * 255),
            (byte)Math.Round((g + m) * 255),
            (byte)Math.Round((b + m) * 255));
    }
}
