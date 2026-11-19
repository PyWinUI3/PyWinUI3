using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage.Streams;
using Windows.UI;

namespace PyWinUI.Host.Rendering;

/// <summary>
/// 把协议属性写进 WinUI 控件。
///
/// 每个属性单独 try/catch：一条属性写错只丢这一条，其余照常渲染。
/// 这是刻意的——调试界面时最怕的是「改了一个字，整个窗口全白」。
///
/// 属性名是 camelCase，与 protocol.md 第 3 节一一对应，
/// 新增属性时先改协议文档，再改这里。
/// </summary>
internal sealed class PropertyApplier
{
    private readonly Action<string> _warn;
    private readonly Action<string, string, JsonObject?, bool> _emit;

    /// <summary>
    /// 当前正在应用属性的节点 id。
    ///
    /// 放在字段里而不是逐层传参：六十多个属性分支里只有右键菜单和图片失败回调
    /// 需要它，为这两处给所有分支加参数不划算。属性应用全程同步跑在 UI 线程，
    /// 不存在重入，所以这个「当前」是安全的。
    /// </summary>
    private string? _nodeId;

    /// <summary>
    /// 创建列表项内部子控件。参数：(父节点 id, 条目序号, 子控件序号, 节点描述)。
    /// 由 FormRenderer 注入——它才有节点表，子控件必须注册进去，
    /// 否则事件回传时按元素反查不到 id。
    /// 为 null 表示宿主不支持（纯单元测试里可直接省略）。
    /// </summary>
    private readonly Func<string, int, int, JsonObject, FrameworkElement?>? _createItemChild;

    /// <summary>
    /// 重建某个列表的 items 之前，清掉上一轮在该列表里创建的子节点。
    /// 不清理的话旧元素会一直挂在节点表里，列表反复刷新就是稳定的内存泄漏。
    /// </summary>
    private readonly Action<string>? _resetItemChildren;

    public PropertyApplier(
        Action<string> warn,
        Action<string, string, JsonObject?, bool> emit,
        Func<string, int, int, JsonObject, FrameworkElement?>? createItemChild = null,
        Action<string>? resetItemChildren = null)
    {
        _warn = warn;
        _emit = emit;
        _createItemChild = createItemChild;
        _resetItemChildren = resetItemChildren;
    }

    public void Apply(string? nodeId, FrameworkElement element, JsonObject? props)
    {
        _nodeId = nodeId;

        if (props is null || props.Count == 0)
        {
            return;
        }

        // 图片解码尺寸要在 src 之前收集好，与 kwargs 的书写顺序无关
        ExtendedProperties.Prepare(element, props);

        foreach (var pair in props)
        {
            try
            {
                // 扩展控件/新增属性优先；返回 false 的交回下面原有的 switch
                if (ExtendedProperties.TryApply(element, pair.Key, pair.Value, _warn))
                {
                    continue;
                }

                ApplyOne(element, pair.Key, pair.Value);
            }
            catch (Exception ex)
            {
                _warn($"属性 \"{pair.Key}\" 应用失败: {ex.Message}");
            }
        }

        // 组合行为放在最后：它依赖 collapsedWidth / expandedWidth 这些兄弟属性，
        // 逐个应用时读不到还没落地的值
        if (ValueParsers.AsBool(props["autoCollapse"]) == true)
        {
            ApplyAutoCollapse(element, props);
        }
    }

    private void ApplyOne(FrameworkElement element, string key, JsonNode? value)
    {
        switch (key)
        {
            // ---------------- 尺寸与定位 ----------------
            case "width":
                if (ValueParsers.AsDouble(value) is { } width) element.Width = width;
                break;
            case "height":
                if (ValueParsers.AsDouble(value) is { } height) element.Height = height;
                break;
            case "minWidth":
                if (ValueParsers.AsDouble(value) is { } minWidth) element.MinWidth = minWidth;
                break;
            case "minHeight":
                if (ValueParsers.AsDouble(value) is { } minHeight) element.MinHeight = minHeight;
                break;
            case "maxWidth":
                if (ValueParsers.AsDouble(value) is { } maxWidth) element.MaxWidth = maxWidth;
                break;
            case "maxHeight":
                if (ValueParsers.AsDouble(value) is { } maxHeight) element.MaxHeight = maxHeight;
                break;
            case "margin":
                if (ValueParsers.AsThickness(value) is { } margin) element.Margin = margin;
                break;
            case "hAlign":
                if (ValueParsers.AsHorizontalAlignment(value) is { } horizontal)
                    element.HorizontalAlignment = horizontal;
                break;
            case "vAlign":
                if (ValueParsers.AsVerticalAlignment(value) is { } vertical)
                    element.VerticalAlignment = vertical;
                break;
            case "visible":
                if (ValueParsers.AsBool(value) is { } visible)
                    element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                break;
            case "opacity":
                if (ValueParsers.AsDouble(value) is { } opacity)
                    element.Opacity = Math.Clamp(opacity, 0, 1);
                break;
            case "cornerRadius":
                ApplyCornerRadius(element, value);
                break;

            // ---------------- 外观 ----------------
            case "background":
                if (element is Button colorButton)
                {
                    ApplyButtonBackground(colorButton, ValueParsers.AsColor(value));
                }
                else if (ValueParsers.AsBrush(value) is { } background)
                {
                    SetBackground(element, background);
                }

                break;
            case "foreground":
                if (ValueParsers.AsBrush(value) is { } foreground)
                    SetForeground(element, foreground);
                break;
            case "borderColor":
                if (ValueParsers.AsBrush(value) is { } borderBrush)
                    SetBorderBrush(element, borderBrush);
                break;
            case "borderWidth":
                if (ValueParsers.AsDouble(value) is { } borderWidth)
                    SetBorderThickness(element, new Thickness(borderWidth));
                break;

            // ---------------- 字体 ----------------
            case "fontSize":
                ApplyFontSize(element, value);
                break;
            case "fontFamily":
                ApplyFontFamily(element, value);
                break;
            case "weight":
                ApplyFontWeight(element, value);
                break;
            case "italic":
                ApplyItalic(element, value);
                break;

            // ---------------- 容器 ----------------
            case "padding":
                ApplyPadding(element, value);
                break;
            case "spacing":
                if (SpacingHost(element) is { } stack && ValueParsers.AsDouble(value) is { } spacing)
                    stack.Spacing = spacing;
                break;
            case "rows":
                if (element is Grid rowGrid) ApplyRowDefinitions(rowGrid, value);
                break;
            case "columns":
                if (element is Grid columnGrid) ApplyColumnDefinitions(columnGrid, value);
                break;
            case "rowGap":
                if (element is Grid rowGapGrid && ValueParsers.AsDouble(value) is { } rowGap)
                    rowGapGrid.RowSpacing = rowGap;
                break;
            case "columnGap":
                if (element is Grid columnGapGrid && ValueParsers.AsDouble(value) is { } columnGap)
                    columnGapGrid.ColumnSpacing = columnGap;
                break;
            case "orientation":
                ApplyOrientation(element, value);
                break;

            // ---------------- Grid 子项定位 ----------------
            case "row":
                if (ValueParsers.AsInt(value) is { } row) Grid.SetRow(element, row);
                break;
            case "column":
                if (ValueParsers.AsInt(value) is { } column) Grid.SetColumn(element, column);
                break;
            case "rowSpan":
                if (ValueParsers.AsInt(value) is { } rowSpan)
                    Grid.SetRowSpan(element, Math.Max(1, rowSpan));
                break;
            case "columnSpan":
                if (ValueParsers.AsInt(value) is { } columnSpan)
                    Grid.SetColumnSpan(element, Math.Max(1, columnSpan));
                break;

            // ---------------- 文本 ----------------
            case "text":
                ApplyText(element, value);
                break;
            case "icon":
                ApplyIcon(element, value);
                break;
            case "navigateUri":
                ApplyNavigateUri(element, value);
                break;
            case "tooltip":
                ApplyTooltip(element, value);
                break;
            case "contextMenu":
                ApplyContextMenu(element, value);
                break;
            case "menu":
                ApplyDropDownMenu(element, value);
                break;

            // autoCollapse 是组合行为，在 Apply() 末尾统一处理。
            // 这里认下名字，免得每个兄弟属性都刷一条「未知属性」。
            case "autoCollapse":
            case "collapsedWidth":
            case "expandedWidth":
            case "hoverExpand":
            case "pinOnSelect":
            case "open":
                break;
            case "placeholder":
                if (element is TextBox placeholderBox && ValueParsers.AsString(value) is { } placeholder)
                    placeholderBox.PlaceholderText = placeholder;
                else if (element is PasswordBox placeholderPwd && ValueParsers.AsString(value) is { } pwdPlaceholder)
                    placeholderPwd.PlaceholderText = pwdPlaceholder;
                else if (element is ComboBox placeholderCombo && ValueParsers.AsString(value) is { } comboPlaceholder)
                    placeholderCombo.PlaceholderText = comboPlaceholder;
                break;
            case "header":
                if (element is ToggleSwitch headerSwitch && ValueParsers.AsString(value) is { } header)
                    headerSwitch.Header = header;
                break;
            case "wrap":
                if (element is TextBlock wrapBlock && ValueParsers.AsBool(value) is { } wrap)
                    wrapBlock.TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap;
                break;
            case "maxLines":
                if (element is TextBlock maxLinesBlock && ValueParsers.AsInt(value) is { } maxLines)
                    maxLinesBlock.MaxLines = maxLines;
                break;
            case "textAlign":
                if (element is TextBlock alignBlock
                    && ValueParsers.AsTextAlignment(value) is { } textAlign)
                    alignBlock.TextAlignment = textAlign;
                break;

            // ---------------- 输入 ----------------
            case "readOnly":
                if (element is TextBox readOnlyBox && ValueParsers.AsBool(value) is { } readOnly)
                    readOnlyBox.IsReadOnly = readOnly;
                break;
            case "multiline":
                if (element is TextBox multiBox
                    && ValueParsers.AsBool(value) is { } multiline
                    && multiline)
                {
                    multiBox.AcceptsReturn = true;
                    multiBox.TextWrapping = TextWrapping.Wrap;
                }
                break;
            case "password":
                // 真正的密码框由 NodeFactory 根据 password 属性直接造 PasswordBox，
                // 这里再设一次说明是 patch 中途改的，不支持——需要重建节点
                if (element is TextBox && ValueParsers.AsBool(value) is true)
                {
                    _warn("password 只能在创建时指定，运行中修改请重建节点");
                }

                break;
            case "maxLength":
                if (element is TextBox lengthBox && ValueParsers.AsInt(value) is { } maxLength)
                    lengthBox.MaxLength = maxLength;
                else if (element is PasswordBox lengthPwd && ValueParsers.AsInt(value) is { } pwdMaxLength)
                    lengthPwd.MaxLength = pwdMaxLength;
                break;

            // ---------------- 选择类 ----------------
            case "items":
                ApplyItems(element, value);
                break;
            case "selectedIndex":
                ApplySelectedIndex(element, value);
                break;
            case "checked":
                ApplyChecked(element, value);
                break;
            case "groupName":
                if (element is RadioButton radio && ValueParsers.AsString(value) is { } groupName)
                    radio.GroupName = groupName;
                break;
            case "variant":
                ApplyVariant(element, value);
                break;
            case "on":
                if (element is ToggleSwitch toggleSwitch && ValueParsers.AsBool(value) is { } on)
                    toggleSwitch.IsOn = on;
                break;

            // ---------------- 数值类 ----------------
            case "value":
                ApplyValue(element, value);
                break;
            case "min":
                ApplyMinimum(element, value);
                break;
            case "max":
                ApplyMaximum(element, value);
                break;
            case "stepFrequency":
                if (element is Slider slider && ValueParsers.AsDouble(value) is { } step)
                    slider.StepFrequency = step;
                break;
            case "repeatDelay":
                if (element is RepeatButton repeatDelay && ValueParsers.AsInt(value) is { } delay)
                    repeatDelay.Delay = delay;
                break;
            case "repeatInterval":
                if (element is RepeatButton repeatInterval && ValueParsers.AsInt(value) is { } interval)
                    repeatInterval.Interval = interval;
                break;
            case "indeterminate":
                if (element is ProgressBar bar && ValueParsers.AsBool(value) is { } indeterminate)
                    bar.IsIndeterminate = indeterminate;
                break;

            // ---------------- WebView2 ----------------
            case "source":
                // 降级占位是 Border，不是 WebView2，这里自然跳过：
                // 缺运行时还去设 Source 只会把占位弄成空白。
                if (element is WebView2 web && ValueParsers.AsString(value) is { } url)
                {
                    if (string.IsNullOrWhiteSpace(url))
                    {
                        break;
                    }

                    if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
                    {
                        web.Source = uri;
                    }
                    else
                    {
                        _warn($"WebView2 source 不是绝对 URL: {url}");
                    }
                }

                break;

            // ---------------- 图片 / 媒体 ----------------
            case "src":
                if (element is Image image)
                    ApplyImageSource(image, value);
                else if (element is MediaPlayerElement media)
                    ApplyMediaSource(media, value);
                else
                    _warn($"src 不适用于 {element.GetType().Name}");
                break;
            case "stretch":
                ApplyStretch(element, value);
                break;

            // ---------------- 进度指示 ----------------
            case "active":
                if (element is ProgressRing ring && ValueParsers.AsBool(value) is { } active)
                    ring.IsActive = active;
                break;
            case "size":
                if (element is ProgressRing sizedRing && ValueParsers.AsDouble(value) is { } size)
                {
                    sizedRing.Width = size;
                    sizedRing.Height = size;
                }
                break;

            // ---------------- 媒体控制 ----------------
            case "autoPlay":
                if (element is MediaPlayerElement autoMedia
                    && ValueParsers.AsBool(value) is { } autoPlay)
                    autoMedia.AutoPlay = autoPlay;
                break;
            case "showControls":
                if (element is MediaPlayerElement ctrlMedia
                    && ValueParsers.AsBool(value) is { } showControls)
                    ctrlMedia.AreTransportControlsEnabled = showControls;
                break;
            case "volume":
                if (element is MediaPlayerElement volMedia
                    && ValueParsers.AsDouble(value) is { } volume)
                    EnsurePlayer(volMedia).Volume = Math.Clamp(volume, 0, 1);
                break;
            case "position":
                // 秒数，用于跳转播放位置
                if (element is MediaPlayerElement posMedia
                    && ValueParsers.AsDouble(value) is { } position)
                    EnsurePlayer(posMedia).PlaybackSession.Position =
                        TimeSpan.FromSeconds(Math.Max(0, position));
                break;
            case "loop":
                if (element is MediaPlayerElement loopMedia
                    && ValueParsers.AsBool(value) is { } loop)
                    EnsurePlayer(loopMedia).IsLoopingEnabled = loop;
                break;
            case "muted":
                if (element is MediaPlayerElement mutedMedia
                    && ValueParsers.AsBool(value) is { } muted)
                    EnsurePlayer(mutedMedia).IsMuted = muted;
                break;
            case "playbackRate":
                if (element is MediaPlayerElement rateMedia
                    && ValueParsers.AsDouble(value) is { } rate)
                    EnsurePlayer(rateMedia).PlaybackSession.PlaybackRate =
                        Math.Clamp(rate, 0.25, 4.0);
                break;

            // ---------------- 分隔线 ----------------
            case "color":
                if (element is Border colorBorder && ValueParsers.AsBrush(value) is { } borderColor)
                    colorBorder.Background = borderColor;
                break;
            case "thickness":
                if (element is Border thicknessBorder && ValueParsers.AsDouble(value) is { } thickness)
                    thicknessBorder.Height = thickness;
                break;

            // ---------------- 交互 ----------------
            case "enabled":
                if (element is Control enabledControl && ValueParsers.AsBool(value) is { } enabled)
                    enabledControl.IsEnabled = enabled;
                break;

            default:
                _warn($"未知属性 \"{key}\"");
                break;
        }
    }

    // ------------------------------------------------------------------
    // 各属性族的落地实现
    // ------------------------------------------------------------------

    /// <summary>
    /// 按钮外观变体。目前只支持 <c>accent</c>（强调色按钮），
    /// 靠切换成系统自带的 <c>AccentButtonStyle</c> 实现，视觉上跟系统一致。
    /// </summary>
    private void ApplyVariant(FrameworkElement element, JsonNode? value)
    {
        if (element is not Button button)
        {
            return;
        }

        var variant = ValueParsers.AsString(value)?.Trim().ToLowerInvariant();

        // normal / default：把之前设过的强调色样式清掉，回到控件默认外观。
        // 侧边栏切换选中项要靠它——只会设 Style 不会清，旧的选中项会一直亮着。
        if (variant is "normal" or "default" or "none")
        {
            button.ClearValue(FrameworkElement.StyleProperty);
            return;
        }

        if (variant is not ("accent" or "primary"))
        {
            return;
        }

        try
        {
            if (Application.Current.Resources.TryGetValue("AccentButtonStyle", out var style)
                && style is Style accentStyle)
            {
                button.Style = accentStyle;
            }
            else
            {
                _warn("找不到 AccentButtonStyle，variant=accent 未生效");
            }
        }
        catch (Exception ex)
        {
            _warn($"variant 应用失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 给按钮上色。
    ///
    /// 只设 <c>Background</c> 是不够的：WinUI 的 Button 模板走主题资源而不是
    /// TemplateBinding，鼠标移上去或按下时会切回系统默认色。必须把三个视觉状态
    /// 的资源一起覆盖，颜色才是真的定住。
    /// </summary>
    private static void ApplyButtonBackground(Button button, Color? color)
    {
        if (color is null)
        {
            return;
        }

        var baseColor = color.Value;
        var hover = Blend(baseColor, Colors.White, 0.14);
        var pressed = Blend(baseColor, Colors.Black, 0.14);
        var foreground = IsDark(baseColor) ? Colors.White : Colors.Black;

        button.Background = new SolidColorBrush(baseColor);
        button.Foreground = new SolidColorBrush(foreground);

        button.Resources["ButtonBackground"] = new SolidColorBrush(baseColor);
        button.Resources["ButtonBackgroundPointerOver"] = new SolidColorBrush(hover);
        button.Resources["ButtonBackgroundPressed"] = new SolidColorBrush(pressed);
        button.Resources["ButtonForeground"] = new SolidColorBrush(foreground);
        button.Resources["ButtonForegroundPointerOver"] = new SolidColorBrush(foreground);
        button.Resources["ButtonForegroundPressed"] = new SolidColorBrush(foreground);
    }

    private static Color Blend(Color baseColor, Color target, double amount)
    {
        byte Mix(byte a, byte b) => (byte)Math.Clamp(Math.Round(a + (b - a) * amount), 0, 255);

        return Color.FromArgb(
            baseColor.A,
            Mix(baseColor.R, target.R),
            Mix(baseColor.G, target.G),
            Mix(baseColor.B, target.B));
    }

    /// <summary>判断颜色是否偏暗，用来决定按钮文字该用白还是黑。</summary>
    private static bool IsDark(Color color)
    {
        var luminance = (0.299 * color.R + 0.587 * color.G + 0.114 * color.B) / 255.0;
        return luminance < 0.6;
    }

    private void ApplyCornerRadius(FrameworkElement element, JsonNode? value)
    {
        if (ValueParsers.AsCornerRadius(value) is not { } radius)
        {
            return;
        }

        switch (element)
        {
            case Control control:
                control.CornerRadius = radius;
                break;
            case Border border:
                border.CornerRadius = radius;
                break;
            case Grid grid:
                grid.CornerRadius = radius;
                break;
            default:
                // Win10 上控件圆角本来就不生效，静默跳过即可
                break;
        }
    }

    private static void SetBackground(FrameworkElement element, Brush brush)
    {
        switch (element)
        {
            case Control control:
                control.Background = brush;
                break;
            case Panel panel:
                panel.Background = brush;
                break;
            case Border border:
                border.Background = brush;
                break;
            default:
                break;
        }
    }

    private static void SetForeground(FrameworkElement element, Brush brush)
    {
        switch (element)
        {
            case Control control:
                control.Foreground = brush;
                break;
            case TextBlock block:
                block.Foreground = brush;
                break;
            // 同上：IconElement 不在 Control 分支里
            case FontIcon icon:
                icon.Foreground = brush;
                break;
            default:
                break;
        }
    }

    private static void SetBorderBrush(FrameworkElement element, Brush brush)
    {
        switch (element)
        {
            case Control control:
                control.BorderBrush = brush;
                break;
            case Border border:
                border.BorderBrush = brush;
                break;
            default:
                break;
        }
    }

    private static void SetBorderThickness(FrameworkElement element, Thickness thickness)
    {
        switch (element)
        {
            case Control control:
                control.BorderThickness = thickness;
                break;
            case Border border:
                border.BorderThickness = thickness;
                break;
            default:
                break;
        }
    }

    private void ApplyFontSize(FrameworkElement element, JsonNode? value)
    {
        if (ValueParsers.AsDouble(value) is not { } size)
        {
            return;
        }

        switch (element)
        {
            case TextBlock block:
                block.FontSize = size;
                break;
            // FontIcon 不是 Control（继承自 IconElement），必须单独放行，
            // 否则 Icon 节点的 fontSize 全部落空。
            case FontIcon icon:
                icon.FontSize = size;
                break;
            case Control control:
                control.FontSize = size;
                break;
            default:
                _warn($"fontSize 不适用于 {element.GetType().Name}");
                break;
        }
    }

    private void ApplyFontFamily(FrameworkElement element, JsonNode? value)
    {
        if (ValueParsers.AsString(value) is not { Length: > 0 } family)
        {
            return;
        }

        var fontFamily = new FontFamily(family);
        switch (element)
        {
            case TextBlock block:
                block.FontFamily = fontFamily;
                break;
            case Control control:
                control.FontFamily = fontFamily;
                break;
            default:
                _warn($"fontFamily 不适用于 {element.GetType().Name}");
                break;
        }
    }

    private void ApplyFontWeight(FrameworkElement element, JsonNode? value)
    {
        if (ValueParsers.AsFontWeight(value) is not { } weight)
        {
            return;
        }

        switch (element)
        {
            case TextBlock block:
                block.FontWeight = weight;
                break;
            case Control control:
                control.FontWeight = weight;
                break;
            default:
                _warn($"weight 不适用于 {element.GetType().Name}");
                break;
        }
    }

    private void ApplyItalic(FrameworkElement element, JsonNode? value)
    {
        if (ValueParsers.AsBool(value) is not { } italic)
        {
            return;
        }

        var style = italic ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal;
        switch (element)
        {
            case TextBlock block:
                block.FontStyle = style;
                break;
            case Control control:
                control.FontStyle = style;
                break;
            default:
                break;
        }
    }

    private void ApplyPadding(FrameworkElement element, JsonNode? value)
    {
        if (ValueParsers.AsThickness(value) is not { } padding)
        {
            return;
        }

        switch (element)
        {
            // WinUI 的 Panel 基类没有 Padding，只有具体容器有
            case StackPanel stack:
                stack.Padding = padding;
                break;
            case Grid grid:
                grid.Padding = padding;
                break;
            case Border border:
                border.Padding = padding;
                break;
            case Control control:
                control.Padding = padding;
                break;
            case TextBlock block:
                block.Padding = padding;
                break;
            default:
                _warn($"padding 不适用于 {element.GetType().Name}");
                break;
        }
    }

    /// <summary>
    /// 找能设置 Spacing 的对象。Scroll 节点外层是 ScrollViewer，
    /// 真正堆叠子元素的是它里面那层 StackPanel。
    /// </summary>
    private static StackPanel? SpacingHost(FrameworkElement element) => element switch
    {
        StackPanel stack => stack,
        ScrollViewer { Content: StackPanel inner } => inner,
        _ => null,
    };

    private void ApplyOrientation(FrameworkElement element, JsonNode? value)
    {
        if (ValueParsers.AsOrientation(value) is not { } orientation)
        {
            return;
        }

        switch (element)
        {
            case StackPanel stack:
                stack.Orientation = orientation;
                break;
            case Slider slider:
                slider.Orientation = orientation;
                break;
            default:
                break;
        }
    }

    private void ApplyRowDefinitions(Grid grid, JsonNode? value)
    {
        if (value is not JsonArray array)
        {
            return;
        }

        grid.RowDefinitions.Clear();
        foreach (var item in array)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = ToGridLength(item) });
        }
    }

    private void ApplyColumnDefinitions(Grid grid, JsonNode? value)
    {
        if (value is not JsonArray array)
        {
            return;
        }

        grid.ColumnDefinitions.Clear();
        foreach (var item in array)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = ToGridLength(item) });
        }
    }

    /// <summary>
    /// 解析 Grid 的尺寸声明：数字为固定像素，<c>"auto"</c> 自适应，
    /// <c>"*"</c> 等分剩余，<c>"2*"</c> 两倍权重。
    /// </summary>
    private GridLength ToGridLength(JsonNode? node)
    {
        if (ValueParsers.AsDouble(node) is { } pixels)
        {
            return new GridLength(pixels);
        }

        var text = ValueParsers.AsString(node)?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return GridLength.Auto;
        }

        if (text.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            return GridLength.Auto;
        }

        if (text == "*")
        {
            return new GridLength(1, GridUnitType.Star);
        }

        if (text.EndsWith('*')
            && double.TryParse(text[..^1], out var starWeight)
            && starWeight > 0)
        {
            return new GridLength(starWeight, GridUnitType.Star);
        }

        _warn($"无法解析 Grid 尺寸 \"{text}\"，按 auto 处理");
        return GridLength.Auto;
    }

    // ------------------------------------------------------------------
    // 按钮外观：图标 + 文字
    // ------------------------------------------------------------------

    /// <summary>
    /// 按钮内容的状态。
    ///
    /// WinUI 的 <c>Content</c> 只能整体替换，所以「文字」和「图标」必须各自留一份记录：
    /// 只改文字时能把图标拼回去，只改图标时也不至于把文字抹掉。
    /// 记录挂在 <see cref="FrameworkElement.Tag"/> 上，随控件生命周期走，不需要额外字典。
    /// </summary>
    private sealed class ButtonFace
    {
        public string? Glyph;
        public string Text = "";
    }

    private void ApplyIcon(FrameworkElement element, JsonNode? value)
    {
        var icon = ValueParsers.AsString(value);

        // 标签页 / 固定选项卡用的是 IconSource，和按钮那套 FontIcon + Content
        // 完全不同，必须在 ContentControl 分支之前拦下来。
        if (element is TabViewItem tab)
        {
            tab.IconSource = BuildTabIcon(icon);
            return;
        }

        // 用 ContentControl 而不是 ButtonBase：SplitButton 不在 ButtonBase 家族里，
        // 但它同样靠 Content 显示内容
        if (element is not ContentControl)
        {
            _warn($"icon 不适用于 {element.GetType().Name}");
            return;
        }

        var face = element.Tag as ButtonFace
                   ?? new ButtonFace { Text = ReadButtonText(element) };
        face.Glyph = string.IsNullOrWhiteSpace(icon) ? null : icon;
        RenderButtonFace(element, face);
    }

    /// <summary>
    /// 标签图标：既接受 Segoe Fluent Icons 的字形（如 <c>"\uE80F"</c>），
    /// 也接受本地图片路径（.png / .jpg / .ico / .bmp / .svg）。
    /// 传空字符串表示去掉图标。
    /// </summary>
    private IconSource? BuildTabIcon(string? icon)
    {
        if (string.IsNullOrWhiteSpace(icon))
        {
            return null;
        }

        var looksLikePath = icon.Contains('\\') || icon.Contains('/')
            || new[] { ".png", ".jpg", ".jpeg", ".ico", ".bmp", ".svg" }
                .Any(ext => icon.EndsWith(ext, StringComparison.OrdinalIgnoreCase));

        if (!looksLikePath)
        {
            return new FontIconSource { Glyph = icon, FontSize = 16 };
        }

        try
        {
            var path = Path.IsPathRooted(icon) ? icon : Path.GetFullPath(icon);
            if (!File.Exists(path))
            {
                _warn($"icon 文件不存在: {path}");
                return null;
            }

            // SVG 要用 SvgImageSource，BitmapImage 不解码矢量图。
            // 固定栅格化成 32px：图标槽是 16 逻辑像素，32 能覆盖 200% DPI，
            // 又不至于像无尺寸 SVG 那样按自然大小撑出巨大位图。
            ImageSource source = Path.GetExtension(path).Equals(
                ".svg", StringComparison.OrdinalIgnoreCase)
                ? new SvgImageSource(new Uri(path))
                {
                    RasterizePixelWidth = 32,
                    RasterizePixelHeight = 32,
                }
                : new BitmapImage(new Uri(path));

            return new ImageIconSource { ImageSource = source };
        }
        catch (Exception ex)
        {
            _warn($"icon 加载失败: {ex.Message}");
            return null;
        }
    }

    private void ApplyButtonText(FrameworkElement element, string text)
    {
        if (element.Tag is not ButtonFace face)
        {
            // 没配图标时保持原来的轻量路径：Content 直接放字符串
            SetButtonContent(element, text);
            return;
        }

        face.Text = text;
        RenderButtonFace(element, face);
    }

    private static string ReadButtonText(FrameworkElement element) =>
        element is ContentControl { Content: string text } ? text : "";

    private static void SetButtonContent(FrameworkElement element, object content)
    {
        if (element is ContentControl target)
        {
            target.Content = content;
        }
    }

    private void RenderButtonFace(FrameworkElement element, ButtonFace face)
    {
        element.Tag = face;

        if (face.Glyph is null)
        {
            SetButtonContent(element, face.Text);
            return;
        }

        // 图标 + 文字的横向组合。用 StackPanel 拼而不是 XAML DataTemplate，
        // 理由和富列表项一样：界面全是运行时代码构造的。
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };

        panel.Children.Add(new FontIcon
        {
            Glyph = face.Glyph,
            FontSize = 16,
            VerticalAlignment = VerticalAlignment.Center,
        });

        if (!string.IsNullOrEmpty(face.Text))
        {
            panel.Children.Add(new TextBlock
            {
                Text = face.Text,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }

        SetButtonContent(element, panel);
    }

    private void ApplyNavigateUri(FrameworkElement element, JsonNode? value)
    {
        if (element is not HyperlinkButton link)
        {
            _warn($"navigateUri 不适用于 {element.GetType().Name}");
            return;
        }

        var uri = ValueParsers.AsString(value);
        if (string.IsNullOrWhiteSpace(uri))
        {
            link.NavigateUri = null;
            return;
        }

        try
        {
            link.NavigateUri = new Uri(uri, UriKind.RelativeOrAbsolute);
        }
        catch (Exception ex)
        {
            _warn($"navigateUri \"{uri}\" 无效: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------
    // 悬停提示与右键菜单
    // ------------------------------------------------------------------

    private void ApplyTooltip(FrameworkElement element, JsonNode? value)
    {
        var text = ValueParsers.AsString(value);
        ToolTipService.SetToolTip(element, string.IsNullOrWhiteSpace(text) ? null : text);
    }

    // ------------------------------------------------------------------
    // 自动收缩（悬停展开 / 焦点在内保持 / 失焦收起）
    // ------------------------------------------------------------------

    /// <summary>
    /// 自动收缩：悬停展开、选中保持、失焦收起。
    ///
    /// 实现整体在 <see cref="AutoCollapse"/> 里。以前宿主这份因为
    /// collapsedWidth / expandedWidth 从未下发而永远不会执行，
    /// 真正生效的是 Python 侧那套 20 步手搓动画；两套行为不一致，
    /// 且 Python 那套带「收起后延时清字」的竞态。现在合并成宿主单点实现。
    /// </summary>
    private void ApplyAutoCollapse(FrameworkElement element, JsonObject props)
    {
        if (_nodeId is null)
        {
            return;
        }

        AutoCollapse.Attach(element, props, _nodeId, _warn, _emit);
    }

    /// <summary>
    /// 右键菜单。数组元素形如
    /// <c>{"label": "复制", "glyph": "\uE8C8", "value": "copy", "enabled": true}</c>，
    /// 分隔线用字符串 <c>"-"</c> 或 <c>{"separator": true}</c>。
    ///
    /// 点击后以 <c>menu</c> 事件回传 <c>{"value": ..., "label": ...}</c>。
    /// 用「一个事件 + value」而不是每条菜单一个事件名：菜单项是数据，
    /// 让 Python 侧按 value 分派，比动态注册一堆事件好维护。
    /// </summary>
    private void ApplyContextMenu(FrameworkElement element, JsonNode? value)
    {
        var flyout = BuildMenu(value, _nodeId);
        element.ContextFlyout = flyout;

        if (flyout is not null)
        {
            HostLog.Write($"[props] contextMenu {_nodeId}: {flyout.Items.Count} 项");
        }
    }

    /// <summary>
    /// SplitButton 的下拉菜单。菜单项格式与 <c>contextMenu</c> 完全一致，
    /// 点击同样回传 <c>menu</c> 事件。
    /// </summary>
    private void ApplyDropDownMenu(FrameworkElement element, JsonNode? value)
    {
        if (element is not SplitButton split)
        {
            _warn($"menu 不适用于 {element.GetType().Name}（目前只有 SplitButton 支持）");
            return;
        }

        split.Flyout = BuildMenu(value, _nodeId);
        HostLog.Write($"[props] splitButton menu {_nodeId}");
    }

    /// <summary>
    /// 把协议里的菜单数组变成 <see cref="MenuFlyout"/>。
    ///
    /// 右键菜单和 SplitButton 下拉共用这一份：菜单项是数据，两处的字段、
    /// 分隔线写法、回传事件都一致，分开写迟早会漂移。
    /// </summary>
    private MenuFlyout? BuildMenu(JsonNode? value, string? nodeId)
    {
        if (value is not JsonArray array || array.Count == 0)
        {
            return null;
        }

        var flyout = new MenuFlyout();

        foreach (var item in array)
        {
            if (IsMenuSeparator(item))
            {
                flyout.Items.Add(new MenuFlyoutSeparator());
                continue;
            }

            if (item is not JsonObject obj)
            {
                _warn("菜单的元素必须是对象或 \"-\"");
                continue;
            }

            var label = ValueParsers.AsString(obj["label"]) ?? "";
            var menuItem = new MenuFlyoutItem { Text = label };

            if (ValueParsers.AsString(obj["glyph"]) is { Length: > 0 } glyph)
            {
                menuItem.Icon = new FontIcon { Glyph = glyph, FontSize = 14 };
            }

            if (ValueParsers.AsBool(obj["enabled"]) is false)
            {
                menuItem.IsEnabled = false;
            }

            var emitted = ValueParsers.AsString(obj["value"]) ?? label;
            menuItem.Click += (_, _) => EmitTo(nodeId, "menu", new JsonObject
            {
                ["value"] = emitted,
                ["label"] = label,
            });

            flyout.Items.Add(menuItem);
        }

        return flyout.Items.Count > 0 ? flyout : null;
    }

    private static bool IsMenuSeparator(JsonNode? node)
    {
        if (node is JsonValue value && value.TryGetValue<string>(out var text))
        {
            return text.Trim() == "-";
        }

        return node is JsonObject obj && ValueParsers.AsBool(obj["separator"]) == true;
    }

    /// <summary>
    /// 回传事件。节点 id 由调用方传入而不是读字段——菜单点击、图片加载失败
    /// 都是异步发生的，那时字段早就指向别的节点了。
    /// </summary>
    private void EmitTo(string? nodeId, string name, JsonObject data)
    {
        if (nodeId is { } id)
        {
            _emit(id, name, data, false);
        }
    }

    private void ApplyText(FrameworkElement element, JsonNode? value)
    {
        var text = ValueParsers.AsString(value) ?? string.Empty;

        // 顺序要紧：CheckBox / RadioButton 也是 ToggleButton 的子类，
        // 必须先按更具体的类型拦下来，否则会掉进 ButtonBase 分支。
        switch (element)
        {
            case TextBlock block:
                block.Text = text;
                break;
            case TextBox box:
                box.Text = text;
                break;
            case PasswordBox passwordBox:
                passwordBox.Password = text;
                break;
            case CheckBox check:
                check.Content = text;
                break;
            case RadioButton radio:
                radio.Content = text;
                break;
            case ToggleSwitch toggle:
                toggle.Header = text;
                break;
            // SplitButton 不继承 ButtonBase，得单独列一条
            case SplitButton split:
                split.Content = text;
                break;
            // Button / HyperlinkButton / ToggleButton / RepeatButton
            case ButtonBase button:
                ApplyButtonText(button, text);
                break;
            default:
                _warn($"text 不适用于 {element.GetType().Name}");
                break;
        }
    }

    private void ApplyItems(FrameworkElement element, JsonNode? value)
    {
        if (value is not JsonArray array)
        {
            return;
        }

        // 只要有一个对象元素，就整体走带图标的富项模式，
        // 好让同一份列表里「纯文字项」和「带图标项」可以混排
        var hasRich = array.Any(node => node is JsonObject);

        // 换 ItemsSource 会丢弃旧列表项，它们对应的子节点要一并从节点表摘掉
        _resetItemChildren?.Invoke(_nodeId ?? "");

        object source = hasRich ? BuildRichItems(array) : BuildPlainItems(array);

        switch (element)
        {
            case ComboBox combo:
                combo.ItemsSource = source;
                break;
            case ListView list:
                list.ItemsSource = source;
                break;
            default:
                _warn($"items 不适用于 {element.GetType().Name}");
                break;
        }

        // 换 ItemsSource 会重建列表项，折叠态的文字显隐需要重新应用
        if (element is ListView)
        {
            AutoCollapse.Refresh(element);
        }
    }

    private static List<string> BuildPlainItems(JsonArray array)
    {
        var list = new List<string>(array.Count);
        foreach (var node in array)
        {
            list.Add(ValueParsers.AsString(node) ?? string.Empty);
        }

        return list;
    }

    private List<FrameworkElement> BuildRichItems(JsonArray array)
    {
        var list = new List<FrameworkElement>(array.Count);
        for (var index = 0; index < array.Count; index++)
        {
            var node = array[index];
            if (node is JsonObject obj)
            {
                // 子控件模式：条目内容是任意节点树，不再局限于「图标 + 文字」
                if (obj["children"] is JsonArray children && children.Count > 0)
                {
                    list.Add(BuildItemWithChildren(index, children));
                    continue;
                }

                list.Add(BuildRichItem(
                    ValueParsers.AsString(obj["text"]) ?? string.Empty,
                    ValueParsers.AsString(obj["glyph"])));
            }
            else
            {
                // 混排时的裸字符串按无图标处理
                list.Add(BuildRichItem(ValueParsers.AsString(node) ?? string.Empty, null));
            }
        }

        return list;
    }

    /// <summary>
    /// 用 children 数组描述一个列表项的内部布局。
    ///
    /// 单个子控件直接用；多个则包一层横向 StackPanel 并留 8px 间距
    /// （想要纵向排布就在 children 里放一个 Column 节点，本层不替用户决定方向）。
    /// </summary>
    private FrameworkElement BuildItemWithChildren(int index, JsonArray children)
    {
        if (_createItemChild is null)
        {
            _warn("当前宿主不支持 items.children（缺少节点注册回调）");
            return new TextBlock { Text = string.Empty };
        }

        var built = new List<FrameworkElement>(children.Count);
        for (var childIndex = 0; childIndex < children.Count; childIndex++)
        {
            if (children[childIndex] is not JsonObject spec)
            {
                _warn($"items[{index}].children[{childIndex}] 不是对象，已跳过");
                continue;
            }

            var element = _createItemChild(_nodeId ?? "", index, childIndex, spec);
            if (element is not null)
            {
                built.Add(element);
            }
        }

        if (built.Count == 1)
        {
            return built[0];
        }

        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };

        foreach (var element in built)
        {
            panel.Children.Add(element);
        }

        return panel;
    }

    /// <summary>
    /// 单个列表项：图标 + 文字的横向排列。
    /// 用 StackPanel 拼而不是 DataTemplate，是因为 DataTemplate 需要 XAML，
    /// 而我们的界面全是运行时代码构造的。
    /// </summary>
    private static FrameworkElement BuildRichItem(string text, string? glyph)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            VerticalAlignment = VerticalAlignment.Center,
        };

        if (!string.IsNullOrEmpty(glyph))
        {
            panel.Children.Add(new FontIcon
            {
                Glyph = glyph,
                FontSize = 16,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }

        panel.Children.Add(new TextBlock
        {
            Text = text,
            VerticalAlignment = VerticalAlignment.Center,
        });

        // 选中事件回传的 value 从这里取——此时 SelectedItem 是个 UIElement，
        // 没法直接当字符串用
        panel.Tag = text;
        return panel;
    }

    private void ApplySelectedIndex(FrameworkElement element, JsonNode? value)
    {
        if (ValueParsers.AsInt(value) is not { } index)
        {
            return;
        }

        switch (element)
        {
            case ComboBox combo:
                combo.SelectedIndex = index;
                break;
            case ListView list:
                list.SelectedIndex = index;
                break;
            default:
                _warn($"selectedIndex 不适用于 {element.GetType().Name}");
                break;
        }
    }

    private void ApplyChecked(FrameworkElement element, JsonNode? value)
    {
        if (ValueParsers.AsBool(value) is not { } isChecked)
        {
            return;
        }

        switch (element)
        {
            case CheckBox check:
                check.IsChecked = isChecked;
                break;
            case RadioButton radio:
                radio.IsChecked = isChecked;
                break;
            case ToggleSwitch toggle:
                toggle.IsOn = isChecked;
                break;
            // 注意：CheckBox / RadioButton 也是 ToggleButton 的子类，
            // 所以这一条必须排在它们后面
            case ToggleButton toggleButton:
                toggleButton.IsChecked = isChecked;
                break;
            default:
                _warn($"checked 不适用于 {element.GetType().Name}");
                break;
        }
    }

    private void ApplyValue(FrameworkElement element, JsonNode? value)
    {
        if (ValueParsers.AsDouble(value) is not { } number)
        {
            return;
        }

        switch (element)
        {
            case Slider slider:
                slider.Value = number;
                break;
            case ProgressBar bar:
                bar.Value = number;
                break;
            default:
                _warn($"value 不适用于 {element.GetType().Name}");
                break;
        }
    }

    private void ApplyMinimum(FrameworkElement element, JsonNode? value)
    {
        if (ValueParsers.AsDouble(value) is not { } minimum)
        {
            return;
        }

        switch (element)
        {
            case Slider slider:
                slider.Minimum = minimum;
                break;
            case ProgressBar bar:
                bar.Minimum = minimum;
                break;
            default:
                break;
        }
    }

    private void ApplyMaximum(FrameworkElement element, JsonNode? value)
    {
        if (ValueParsers.AsDouble(value) is not { } maximum)
        {
            return;
        }

        switch (element)
        {
            case Slider slider:
                slider.Maximum = maximum;
                break;
            case ProgressBar bar:
                bar.Maximum = maximum;
                break;
            default:
                break;
        }
    }

    private void ApplyImageSource(FrameworkElement element, JsonNode? value)
    {
        if (element is not Image image)
        {
            _warn($"src 不适用于 {element.GetType().Name}");
            return;
        }

        var source = ValueParsers.AsString(value);
        if (string.IsNullOrWhiteSpace(source))
        {
            return;
        }

        // 闭包会在图片异步加载完成后才跑，节点 id 必须先抓在手里
        var nodeId = _nodeId;

        if (source.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            LoadImageFromDataUri(image, source, nodeId);
            return;
        }

        try
        {
            var bitmap = new BitmapImage();
            ExtendedProperties.ApplyDecodeHints(image, bitmap);
            bitmap.ImageFailed += (_, e) => EmitTo(nodeId, "imageFailed", new JsonObject
            {
                ["error"] = e.ErrorMessage ?? "unknown",
                ["src"] = source,
            });
            bitmap.UriSource = new Uri(source, UriKind.RelativeOrAbsolute);
            image.Source = bitmap;
        }
        catch (Exception ex)
        {
            _warn($"图片 \"{source}\" 加载失败: {ex.Message}");
            EmitTo(nodeId, "imageFailed", new JsonObject
            {
                ["error"] = ex.Message,
                ["src"] = source,
            });
        }
    }

    /// <summary>
    /// 解码 <c>data:image/png;base64,...</c>。
    ///
    /// 给「图是程序生成的」场景用——截图、绘图结果直接塞进界面，
    /// 不必先写一个临时文件再让宿主去读。
    /// </summary>
    private async void LoadImageFromDataUri(Image image, string dataUri, string? nodeId)
    {
        try
        {
            var comma = dataUri.IndexOf(',');
            if (comma < 0)
            {
                throw new FormatException("data URI 缺少逗号分隔的正文");
            }

            var meta = dataUri[5..comma];
            var payload = dataUri[(comma + 1)..];

            var bytes = meta.Contains("base64", StringComparison.OrdinalIgnoreCase)
                ? Convert.FromBase64String(payload)
                : Encoding.UTF8.GetBytes(Uri.UnescapeDataString(payload));

            var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(bytes);
                await writer.StoreAsync();
                await writer.FlushAsync();
                writer.DetachStream();
            }

            stream.Seek(0);

            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
            stream.Dispose();

            image.Source = bitmap;
        }
        catch (Exception ex)
        {
            _warn($"data URI 图片解码失败: {ex.Message}");
            EmitTo(nodeId, "imageFailed", new JsonObject
            {
                ["error"] = ex.Message,
                ["src"] = "data:",
            });
        }
    }

    /// <summary>
    /// 取（必要时创建）MediaPlayerElement 的播放器。
    ///
    /// MediaPlayerElement 初始没有 MediaPlayer，必须显式 SetMediaPlayer 才能设音量、
    /// 设置跳转位置这些属性——直接读 MediaPlayer 会拿到 null。
    /// </summary>
    private static MediaPlayer EnsurePlayer(MediaPlayerElement element)
    {
        if (element.MediaPlayer is { } existing)
        {
            return existing;
        }

        var player = new MediaPlayer();
        element.SetMediaPlayer(player);
        return player;
    }

    /// <summary>
    /// 媒体源。本地路径和 http(s) URL 都能直接用 Uri 表达，
    /// 所以统一走 <see cref="MediaSource.CreateFromUri"/>。
    /// </summary>
    private void ApplyMediaSource(MediaPlayerElement element, JsonNode? value)
    {
        var source = ValueParsers.AsString(value);
        if (string.IsNullOrWhiteSpace(source))
        {
            return;
        }

        try
        {
            EnsurePlayer(element).Source =
                MediaSource.CreateFromUri(new Uri(source, UriKind.RelativeOrAbsolute));
        }
        catch (Exception ex)
        {
            _warn($"媒体 \"{source}\" 加载失败: {ex.Message}");
        }
    }

    private void ApplyStretch(FrameworkElement element, JsonNode? value)
    {
        if (ValueParsers.AsStretch(value) is not { } stretch)
        {
            return;
        }

        switch (element)
        {
            case Image image:
                image.Stretch = stretch;
                break;
            case MediaPlayerElement media:
                media.Stretch = stretch;
                break;
            default:
                _warn($"stretch 不适用于 {element.GetType().Name}");
                break;
        }
    }
}
