using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Windows.Foundation;
using Windows.UI;
using XamlPath = Microsoft.UI.Xaml.Shapes.Path;

namespace PyWinUI.Host.Rendering;

/// <summary>
/// 扩展属性分发：新控件的属性 + 首版漏掉的通用属性。
///
/// 调用约定：PropertyApplier 在每条属性上先问这里，返回 true 表示已消费，
/// 返回 false 表示「这个 key / 控件组合不归我管」，交回原有 switch。
/// 这样基础控件的既有行为一行都不用改。
///
/// 已识别但赋值失败的属性也返回 true（并打 warn），否则会掉回主 switch
/// 触发一条误导性的「未知属性」告警。
/// </summary>
internal static class ExtendedProperties
{
    // ------------------------------------------------------------------
    // 图片解码提示
    // ------------------------------------------------------------------

    private sealed class DecodeHints
    {
        public int Width;
        public int Height;
    }

    /// <summary>
    /// DecodePixelWidth/Height 必须在设置 UriSource 之前落到 BitmapImage 上，
    /// 但 props 是字典，用户写 kwargs 的顺序不保证。所以先整包收集一遍，
    /// ApplyImageSource 创建 BitmapImage 时再来取。
    /// ConditionalWeakTable 让提示跟着控件走，节点销毁后自动回收。
    /// </summary>
    private static readonly ConditionalWeakTable<FrameworkElement, DecodeHints> ImageHints = new();

    public static void Prepare(FrameworkElement element, JsonObject? props)
    {
        if (props is null)
        {
            return;
        }

        if (props["decodePixelWidth"] is null && props["decodePixelHeight"] is null)
        {
            return;
        }

        var hints = ImageHints.GetValue(element, _ => new DecodeHints());
        hints.Width = (int)(ValueParsers.AsDouble(props["decodePixelWidth"]) ?? 0);
        hints.Height = (int)(ValueParsers.AsDouble(props["decodePixelHeight"]) ?? 0);
    }

    public static void ApplyDecodeHints(Image image, BitmapImage bitmap)
    {
        if (!ImageHints.TryGetValue(image, out var hints))
        {
            return;
        }

        if (hints.Width > 0)
        {
            bitmap.DecodePixelWidth = hints.Width;
        }

        if (hints.Height > 0)
        {
            bitmap.DecodePixelHeight = hints.Height;
        }
    }

    // ------------------------------------------------------------------
    // 分发
    // ------------------------------------------------------------------

    public static bool TryApply(
        FrameworkElement element,
        string key,
        JsonNode? value,
        Action<string> warn)
    {
        try
        {
            return Apply(element, key, value, warn);
        }
        catch (Exception ex)
        {
            warn($"扩展属性 \"{key}\" 应用失败: {ex.Message}");
            return true;
        }
    }

    private static bool Apply(
        FrameworkElement e,
        string key,
        JsonNode? v,
        Action<string> warn)
    {
        // TabPage（TabViewItem）与固定选项卡是新增元素，主 switch 不认识它们。
        // 先在这里消费专有属性，避免掉回去报一条误导性的「未知属性」。
        // WebView2 的设置要等 CoreWebView2 初始化，先交给支持层缓存；
        // 降级占位是 Border，这里认下名字并静默忽略，别刷「未知属性」。
        if (WebView2Support.IsSettingKey(key))
        {
            if (e is WebView2 settingWeb)
            {
                WebView2Support.SetSetting(settingWeb, key, v, warn);
            }

            return true;
        }

        if (e is TabViewItem tabPage)
        {
            switch (key)
            {
                case "text":
                    // 用 TextBlock 承载标题而不是字符串：只有 TextBlock 能设
                    // TextTrimming=CharacterEllipsis，长标题才会变成 "标题…"。
                    // 宽度约束（minWidth / maxWidth）施加在 TabViewItem 上，
                    // 文本会在可用宽度内自动省略。
                    ExtendedControls.SetTabHeader(tabPage, ValueParsers.AsString(v) ?? "");
                    return true;

                case "closable":
                    tabPage.IsClosable = ValueParsers.AsBool(v) ?? true;
                    return true;
            }
        }

        if (e is TabView tabStrip)
        {
            switch (key)
            {
                case "canReorderTabs":
                    tabStrip.CanReorderTabs = ValueParsers.AsBool(v) ?? true;
                    return true;
                // WinUI 默认是 Equal：所有标签平分整条宽度，两三个标签时会被拉得
                // 很宽、关闭按钮漂到很远。演示和大多数工具界面要的是按内容收缩。
                case "tabWidthMode":
                    tabStrip.TabWidthMode = (ValueParsers.AsString(v) ?? "").ToLowerInvariant() switch
                    {
                        "equal" => TabViewWidthMode.Equal,
                        "compact" => TabViewWidthMode.Compact,
                        "sizetocontent" or "size_to_content" => TabViewWidthMode.SizeToContent,
                        _ => tabStrip.TabWidthMode,
                    };
                    return true;
            }
        }

        switch (key)
        {
            // ================= 通用 FrameworkElement =================
            case "name":
                e.Name = ValueParsers.AsString(v) ?? "";
                return true;
            case "tag":
                e.Tag = ValueParsers.AsString(v);
                return true;
            case "tabIndex":
                if (e is Control tabControl && ValueParsers.AsInt(v) is { } tabIndex)
                    tabControl.TabIndex = tabIndex;
                return true;
            case "isTabStop":
                if (e is Control stopControl && ValueParsers.AsBool(v) is { } isTabStop)
                    stopControl.IsTabStop = isTabStop;
                return true;
            case "allowDrop":
                if (ValueParsers.AsBool(v) is { } allowDrop)
                    e.AllowDrop = allowDrop;
                return true;
            case "isHitTestVisible":
                if (ValueParsers.AsBool(v) is { } hitTest)
                    e.IsHitTestVisible = hitTest;
                return true;
            case "flowDirection":
                e.FlowDirection = ValueParsers.AsString(v) == "rtl"
                    ? FlowDirection.RightToLeft
                    : FlowDirection.LeftToRight;
                return true;

            // ================= 输入增强 =================
            case "isSpellCheckEnabled":
                if (e is TextBox spell && ValueParsers.AsBool(v) is { } spellCheck)
                    spell.IsSpellCheckEnabled = spellCheck;
                return true;
            case "acceptsReturn":
                if (e is TextBox ret && ValueParsers.AsBool(v) is { } acceptsReturn)
                    ret.AcceptsReturn = acceptsReturn;
                return true;
            case "selectionStart":
                if (e is TextBox selStart && ValueParsers.AsInt(v) is { } start)
                    selStart.SelectionStart = Math.Clamp(start, 0, selStart.Text.Length);
                return true;
            case "selectionLength":
                if (e is TextBox selLen && ValueParsers.AsInt(v) is { } length)
                    selLen.SelectionLength = Math.Clamp(length, 0, Math.Max(0, selLen.Text.Length - selLen.SelectionStart));
                return true;
            case "isReadOnly":
            case "readOnly":
                if (e is TextBox readOnly && ValueParsers.AsBool(v) is { } ro)
                    readOnly.IsReadOnly = ro;
                else if (e is RichEditBox richRo && ValueParsers.AsBool(v) is { } richReadOnly)
                    richRo.IsReadOnly = richReadOnly;
                return true;
            case "decodePixelWidth":
            case "decodePixelHeight":
                // 已在 Prepare() 里收集， ApplyImageSource 会取用；这里只需认下名字
                if (e is not Image)
                    warn($"{key} 只适用于 Image");
                return true;
            case "isEditable":
                if (e is ComboBox editable && ValueParsers.AsBool(v) is { } isEditable)
                    editable.IsEditable = isEditable;
                return true;
            case "isTextSearchEnabled":
                if (e is ComboBox searchable && ValueParsers.AsBool(v) is { } search)
                    searchable.IsTextSearchEnabled = search;
                return true;
            case "selectionMode":
                if (e is ListViewBase listBase && ValueParsers.AsString(v) is { } mode)
                {
                    listBase.SelectionMode = mode.ToLowerInvariant() switch
                    {
                        "none" => ListViewSelectionMode.None,
                        "multiple" => ListViewSelectionMode.Multiple,
                        "extended" => ListViewSelectionMode.Extended,
                        _ => ListViewSelectionMode.Single,
                    };
                }
                else if (e is CalendarView calendar && ValueParsers.AsString(v) is { } calMode)
                {
                    calendar.SelectionMode = calMode.ToLowerInvariant() switch
                    {
                        "multiple" => CalendarViewSelectionMode.Multiple,
                        "none" => CalendarViewSelectionMode.None,
                        _ => CalendarViewSelectionMode.Single,
                    };
                }

                return true;
            case "isFullWindow":
                if (e is MediaPlayerElement fullMedia && ValueParsers.AsBool(v) is { } full)
                    fullMedia.IsFullWindow = full;
                return true;
            case "verticalScrollBarVisibility":
                if (e is ScrollViewer vertical && ValueParsers.AsString(v) is { } verticalMode)
                    vertical.VerticalScrollBarVisibility = ParseScrollBarVisibility(verticalMode);
                return true;
            case "horizontalScrollBarVisibility":
                if (e is ScrollViewer horizontal && ValueParsers.AsString(v) is { } horizontalMode)
                    horizontal.HorizontalScrollBarVisibility = ParseScrollBarVisibility(horizontalMode);
                return true;

            // ================= 扩展控件：与旧 switch 同名的 key =================
            // 只在这些扩展控件上接管；其他控件返回 false，原有逻辑照常生效。
            case "text":
                return ApplyExtendedText(e, v, warn);
            case "richText":
                return ApplyRichText(e, v, warn);
            case "glyph":
                // Icon 节点的字形（Segoe Fluent Icons 码点，如 "\uE8A5"）
                if (e is FontIcon glyphIcon && ValueParsers.AsString(v) is { } glyphText)
                {
                    glyphIcon.Glyph = glyphText;
                    return true;
                }

                return false;
            case "underline":
                // TextBlock 的下划线。富文本片段有独立写法，这里是整块开关。
                if (e is TextBlock underlineBlock && ValueParsers.AsBool(v) is { } underlineOn)
                {
                    underlineBlock.TextDecorations = underlineOn
                        ? Windows.UI.Text.TextDecorations.Underline
                        : Windows.UI.Text.TextDecorations.None;
                    return true;
                }

                return false;

            // ================= 画布与图元 =================
            case "left":
                // Canvas 的绝对定位附加属性；不在 Canvas 里时设置也无害
                if (ValueParsers.AsDouble(v) is { } left)
                {
                    Canvas.SetLeft(e, left);
                    return true;
                }

                return false;

            case "top":
                if (ValueParsers.AsDouble(v) is { } top)
                {
                    Canvas.SetTop(e, top);
                    return true;
                }

                return false;

            case "zIndex":
                // 同一 Canvas 内的叠放次序：数值大的盖在上面。
                // 拖动、选中高亮这类场景必须能把目标临时提到最前。
                if (ValueParsers.AsInt(v) is { } zIndex)
                {
                    Canvas.SetZIndex(e, zIndex);
                    return true;
                }

                return false;

            case "fill":
                if (e is Shape fillShape && ValueParsers.AsBrush(v) is { } fillBrush)
                {
                    fillShape.Fill = fillBrush;
                    return true;
                }

                return false;

            case "stroke":
                if (e is Shape strokeShape && ValueParsers.AsBrush(v) is { } strokeBrush)
                {
                    strokeShape.Stroke = strokeBrush;
                    return true;
                }

                return false;

            case "strokeWidth":
                if (e is Shape widthShape && ValueParsers.AsDouble(v) is { } strokeWidth)
                {
                    widthShape.StrokeThickness = strokeWidth;
                    return true;
                }

                return false;

            case "d":
                // Path 的几何数据，直接吃 SVG 的 d 语法（XAML 路径标记与之基本兼容）
                if (e is XamlPath pathNode)
                {
                    var data = ValueParsers.AsString(v);
                    if (string.IsNullOrWhiteSpace(data))
                    {
                        pathNode.Data = null;
                        return true;
                    }

                    try
                    {
                        pathNode.Data = ParsePathData(data);
                    }
                    catch (Exception ex)
                    {
                        warn($"path d 解析失败: {ex.Message}");
                    }

                    return true;
                }

                return false;

            case "rotate":
                if (ValueParsers.AsDouble(v) is { } angle)
                {
                    e.RenderTransformOrigin = new Point(0.5, 0.5);
                    e.RenderTransform = new CompositeTransform { Rotation = angle };
                    return true;
                }

                return false;

            // 完整变换：旋转 / 缩放 / 平移一次给齐，绕 originX/originY（0-1 比例）生效
            case "transform":
                if (v is JsonObject transform)
                {
                    var scaleX = ValueParsers.AsDouble(transform["scaleX"]) ?? 1;
                    var scaleY = ValueParsers.AsDouble(transform["scaleY"]) ?? 1;
                    var rotation = ValueParsers.AsDouble(transform["rotate"]) ?? 0;
                    var translateX = ValueParsers.AsDouble(transform["translateX"]) ?? 0;
                    var translateY = ValueParsers.AsDouble(transform["translateY"]) ?? 0;
                    var originX = ValueParsers.AsDouble(transform["originX"]) ?? 0.5;
                    var originY = ValueParsers.AsDouble(transform["originY"]) ?? 0.5;

                    e.RenderTransformOrigin = new Point(originX, originY);
                    e.RenderTransform = new CompositeTransform
                    {
                        ScaleX = scaleX,
                        ScaleY = scaleY,
                        Rotation = rotation,
                        TranslateX = translateX,
                        TranslateY = translateY,
                    };
                    return true;
                }

                return false;

            // ---- 描边细节 ----
            case "strokeDashArray":
                if (e is Shape dashShape && v is JsonArray dashArray)
                {
                    var dashes = new DoubleCollection();
                    foreach (var item in dashArray)
                    {
                        if (ValueParsers.AsDouble(item) is { } dashValue && dashValue >= 0)
                        {
                            dashes.Add(dashValue);
                        }
                    }

                    dashShape.StrokeDashArray = dashes;
                    return true;
                }

                return false;

            case "lineCap":
                if (e is Shape capShape && ValueParsers.AsString(v) is { } cap)
                {
                    capShape.StrokeStartLineCap = ParsePenCap(cap);
                    capShape.StrokeEndLineCap = ParsePenCap(cap);
                    return true;
                }

                return false;

            case "lineJoin":
                if (e is Shape joinShape && ValueParsers.AsString(v) is { } join)
                {
                    joinShape.StrokeLineJoin = join.ToLowerInvariant() switch
                    {
                        "round" => PenLineJoin.Round,
                        "bevel" => PenLineJoin.Bevel,
                        _ => PenLineJoin.Miter,
                    };
                    return true;
                }

                return false;

            // ---- Line 端点 ----
            case "x1":
                if (e is Line lineX1 && ValueParsers.AsDouble(v) is { } x1)
                {
                    lineX1.X1 = x1;
                    return true;
                }

                return false;

            case "y1":
                if (e is Line lineY1 && ValueParsers.AsDouble(v) is { } y1)
                {
                    lineY1.Y1 = y1;
                    return true;
                }

                return false;

            case "x2":
                if (e is Line lineX2 && ValueParsers.AsDouble(v) is { } x2)
                {
                    lineX2.X2 = x2;
                    return true;
                }

                return false;

            case "y2":
                if (e is Line lineY2 && ValueParsers.AsDouble(v) is { } y2)
                {
                    lineY2.Y2 = y2;
                    return true;
                }

                return false;

            // ---- 多边形顶点：[[x,y], ...] ----
            case "points":
                if (e is Polygon || e is Polyline)
                {
                    if (v is not JsonArray points)
                    {
                        return true;
                    }

                    var collection = new PointCollection();
                    foreach (var item in points)
                    {
                        if (item is JsonArray pair && pair.Count >= 2)
                        {
                            var px = ValueParsers.AsDouble(pair[0]);
                            var py = ValueParsers.AsDouble(pair[1]);
                            if (px is { } pxValue && py is { } pyValue)
                            {
                                collection.Add(new Point(pxValue, pyValue));
                                continue;
                            }
                        }

                        warn("points 需要 [[x, y], ...] 形式的数组");
                    }

                    if (e is Polygon polygon)
                    {
                        polygon.Points = collection;
                    }
                    else
                    {
                        ((Polyline)e).Points = collection;
                    }

                    return true;
                }

                return false;

            // ---- Rect 圆角 ----
            case "radiusX":
                if (e is Rectangle rectX && ValueParsers.AsDouble(v) is { } radiusX)
                {
                    rectX.RadiusX = radiusX;
                    return true;
                }

                return false;

            case "radiusY":
                if (e is Rectangle rectY && ValueParsers.AsDouble(v) is { } radiusY)
                {
                    rectY.RadiusY = radiusY;
                    return true;
                }

                return false;

            case "view":
                // 画布视图变换：一次给齐缩放和平移，避免三个属性互相覆盖
                if (e is Canvas viewCanvas && v is JsonObject view)
                {
                    var scale = ValueParsers.AsDouble(view["scale"]) ?? 1;
                    var offsetX = ValueParsers.AsDouble(view["offsetX"]) ?? 0;
                    var offsetY = ValueParsers.AsDouble(view["offsetY"]) ?? 0;
                    viewCanvas.RenderTransformOrigin = new Point(0, 0);
                    viewCanvas.RenderTransform = new CompositeTransform
                    {
                        ScaleX = scale,
                        ScaleY = scale,
                        TranslateX = offsetX,
                        TranslateY = offsetY,
                    };
                    return true;
                }

                return false;

            case "shapes":
                // Win2D 画布的图元数组：一条 patch 换整块画面，由 GPU 重绘
                if (e is CanvasControl canvas2d && v is JsonArray shapes)
                {
                    Canvas2DSupport.SetShapes(canvas2d, shapes);
                    return true;
                }

                return false;

            case "mesh":
                // D3D 视口的几何：顶点 + 索引 + 颜色
                if (e is SwapChainPanel viewport && v is JsonObject mesh)
                {
                    Viewport3DSupport.SetMesh(viewport, mesh);
                    return true;
                }

                return false;

            case "camera":
                // D3D 视口的轨道相机：yaw / pitch / distance
                if (e is SwapChainPanel cameraViewport && v is JsonObject camera)
                {
                    Viewport3DSupport.SetCamera(cameraViewport, camera);
                    return true;
                }

                return false;
            case "value":
                return ApplyExtendedValue(e, v, warn);
            case "items":
                return v is JsonArray array && ApplyExtendedItems(e, array, warn);
            case "selectedIndex":
                return ApplyExtendedSelectedIndex(e, v);
            case "min":
                if (e is NumberBox minBox && ValueParsers.AsDouble(v) is { } min)
                {
                    minBox.Minimum = min;
                    return true;
                }
                return false;
            case "max":
                if (e is NumberBox maxBox && ValueParsers.AsDouble(v) is { } max)
                {
                    maxBox.Maximum = max;
                    return true;
                }
                return false;
            case "stepFrequency":
                if (e is NumberBox stepBox && ValueParsers.AsDouble(v) is { } step)
                {
                    stepBox.SmallChange = step;
                    return true;
                }
                return false;
            case "header":
                return ApplyExtendedHeader(e, v);
            case "placeholder":
                return ApplyExtendedPlaceholder(e, v);
            case "stretch":
                if (e is Viewbox viewbox && ValueParsers.AsStretch(v) is { } stretch)
                {
                    viewbox.Stretch = stretch;
                    return true;
                }
                return false;
            case "color":
                if (e is CustomColorPicker picker && ValueParsers.AsColor(v) is { } color)
                {
                    picker.Color = color;
                    return true;
                }
                return false;
            case "checked":
                if (e is ToggleSplitButton splitToggle && ValueParsers.AsBool(v) is { } isChecked)
                {
                    splitToggle.IsChecked = isChecked;
                    return true;
                }
                return false;

            // ================= 扩展控件：新增 key =================
            case "date":
                return ApplyDate(e, v, warn);
            case "time":
                return ApplyTime(e, v, warn);
            case "severity":
                if (e is InfoBar infoSeverity && ValueParsers.AsString(v) is { } severity)
                {
                    infoSeverity.Severity = severity.ToLowerInvariant() switch
                    {
                        "success" => InfoBarSeverity.Success,
                        "warning" or "warn" => InfoBarSeverity.Warning,
                        "error" => InfoBarSeverity.Error,
                        _ => InfoBarSeverity.Informational,
                    };
                }
                return true;
            case "message":
                if (e is InfoBar infoMessage)
                    infoMessage.Message = ValueParsers.AsString(v) ?? "";
                return true;
            case "title":
                if (e is InfoBar infoTitle)
                    infoTitle.Title = ValueParsers.AsString(v) ?? "";
                return true;
            case "isOpen":
                if (e is InfoBar infoOpen && ValueParsers.AsBool(v) is { } isOpen)
                    infoOpen.IsOpen = isOpen;
                return true;
            case "isClosable":
                if (e is InfoBar infoClosable && ValueParsers.AsBool(v) is { } isClosable)
                    infoClosable.IsClosable = isClosable;
                return true;
            case "isExpanded":
                if (e is Expander expander && ValueParsers.AsBool(v) is { } isExpanded)
                    expander.IsExpanded = isExpanded;
                return true;
            case "expandDirection":
                if (e is Expander direction && ValueParsers.AsString(v) == "up")
                    direction.ExpandDirection = ExpandDirection.Up;
                return true;
            case "displayName":
                if (e is PersonPicture person)
                    person.DisplayName = ValueParsers.AsString(v) ?? "";
                return true;
            case "initials":
                if (e is PersonPicture initials)
                    initials.Initials = ValueParsers.AsString(v) ?? "";
                return true;
            case "paneDisplayMode":
                if (e is NavigationView nav && ValueParsers.AsString(v) is { } paneMode)
                {
                    nav.PaneDisplayMode = paneMode.ToLowerInvariant() switch
                    {
                        "top" => NavigationViewPaneDisplayMode.Top,
                        "compact" or "leftcompact" => NavigationViewPaneDisplayMode.LeftCompact,
                        "minimal" or "leftminimal" => NavigationViewPaneDisplayMode.LeftMinimal,
                        "auto" => NavigationViewPaneDisplayMode.Auto,
                        _ => NavigationViewPaneDisplayMode.Left,
                    };
                }
                return true;
            case "isPaneOpen":
                if (e is NavigationView pane && ValueParsers.AsBool(v) is { } paneOpen)
                    pane.IsPaneOpen = paneOpen;
                return true;
            case "openPaneLength":
                if (e is NavigationView openPane && ValueParsers.AsDouble(v) is { } openLength)
                    openPane.OpenPaneLength = openLength;
                return true;
            case "compactPaneLength":
                if (e is NavigationView compactPane && ValueParsers.AsDouble(v) is { } compactLength)
                    compactPane.CompactPaneLength = compactLength;
                return true;
            case "isBackEnabled":
                if (e is NavigationView backNav && ValueParsers.AsBool(v) is { } backEnabled)
                    backNav.IsBackEnabled = backEnabled;
                return true;
            case "isSettingsVisible":
                if (e is NavigationView settings && ValueParsers.AsBool(v) is { } settingsVisible)
                    settings.IsSettingsVisible = settingsVisible;
                return true;
            case "spin":
                if (e is NumberBox spinBox && ValueParsers.AsString(v) is { } spin)
                {
                    spinBox.SpinButtonPlacementMode = spin.ToLowerInvariant() switch
                    {
                        "hidden" or "none" => NumberBoxSpinButtonPlacementMode.Hidden,
                        "inline" => NumberBoxSpinButtonPlacementMode.Inline,
                        _ => NumberBoxSpinButtonPlacementMode.Compact,
                    };
                }
                return true;
            case "isClearEnabled":
                if (e is RatingControl clearRating && ValueParsers.AsBool(v) is { } clear)
                    clearRating.IsClearEnabled = clear;
                return true;
            case "maxRating":
                if (e is RatingControl maxRating && ValueParsers.AsInt(v) is { } ratingMax)
                    maxRating.MaxRating = ratingMax;
                return true;
            case "queryText":
                if (e is AutoSuggestBox query)
                    query.Text = ValueParsers.AsString(v) ?? "";
                return true;
            case "content":
                if (e is DropDownButton drop)
                    drop.Content = ValueParsers.AsString(v) ?? "";
                else if (e is ToggleSplitButton toggleSplit)
                    toggleSplit.Content = ValueParsers.AsString(v) ?? "";
                return true;
            case "maxColumns":
                if (e is RadioButtons radios && ValueParsers.AsInt(v) is { } columns)
                    radios.MaxColumns = columns;
                return true;
            case "isAddTabButtonVisible":
                if (e is TabView tabs && ValueParsers.AsBool(v) is { } addVisible)
                    tabs.IsAddTabButtonVisible = addVisible;
                return true;
            case "panePriority":
                if (e is TwoPaneView twoPane && ValueParsers.AsString(v) is { } priority)
                    twoPane.PanePriority = priority.ToLowerInvariant() == "pane2"
                        ? TwoPaneViewPriority.Pane2
                        : TwoPaneViewPriority.Pane1;
                return true;
            case "isMoreButtonVisible":
                // 自绘调色框没有"更多"按钮这一概念，属性保留以兼容旧代码，
                // 消费掉但不产生视觉效果（避免报未知属性告警）。
                return e is CustomColorPicker;

            default:
                return false;
        }
    }

    // ------------------------------------------------------------------
    // 与主 switch 同名的 key：只接管扩展控件
    // ------------------------------------------------------------------

    private static PenLineCap ParsePenCap(string cap) => cap.ToLowerInvariant() switch
    {
        "round" => PenLineCap.Round,
        "square" => PenLineCap.Square,
        _ => PenLineCap.Flat,
    };

    /// <summary>
    /// 把 SVG 路径标记（M / L / H / V / C / S / Q / T / A / Z）解析成 PathGeometry。
    ///
    /// 不用 XamlReader：WinUI 3 的 XamlReader 不接受 <c>Data="M0,0 L10,10"</c> 这种
    /// 字符串（实测连最简单的路径都报 "Value does not fall within the expected range"），
    /// 所以这里自己做词法 + 构造，既可靠也不依赖 XAML 解析器。
    ///
    /// 支持相对命令（小写）与隐式重复参数（如 "L 1 2 3 4" 等于两段）。
    /// 弧命令 A 直接映射到 ArcSegment，不转成贝塞尔（WinUI 原生支持弧）。
    /// </summary>
    private static Geometry? ParsePathData(string data)
    {
        if (string.IsNullOrWhiteSpace(data))
        {
            return null;
        }

        var tokens = TokenizePath(data);
        if (tokens.Count == 0)
        {
            return null;
        }

        var geometry = new PathGeometry();
        PathFigure? figure = null;

        double curX = 0, curY = 0;
        double startX = 0, startY = 0;
        // 上一个控制点，供 S（三次）与 T（二次）做镜像反射
        double ctrlX = 0, ctrlY = 0;
        char previous = '\0';

        var index = 0;
        var command = 'M';

        bool HasNumber() => index < tokens.Count && tokens[index] is double;

        double ReadNumber()
        {
            var value = (double)tokens[index];
            index++;
            return value;
        }

        PathFigure Figure()
        {
            if (figure is null)
            {
                figure = new PathFigure { StartPoint = new Point(curX, curY), IsClosed = false };
                geometry.Figures.Add(figure);
            }

            return figure;
        }

        while (index < tokens.Count)
        {
            if (tokens[index] is char explicitCommand)
            {
                command = explicitCommand;
                index++;

                if (char.ToUpperInvariant(command) == 'Z')
                {
                    if (figure is not null)
                    {
                        figure.IsClosed = true;
                    }

                    curX = startX;
                    curY = startY;
                    previous = command;
                    continue;
                }
            }

            var relative = char.IsLower(command);
            var upper = char.ToUpperInvariant(command);

            switch (upper)
            {
                case 'M':
                {
                    if (!HasNumber())
                    {
                        return geometry;
                    }

                    var x = ReadNumber();
                    var y = ReadNumber();
                    if (relative)
                    {
                        x += curX;
                        y += curY;
                    }

                    curX = x;
                    curY = y;
                    startX = x;
                    startY = y;
                    figure = new PathFigure { StartPoint = new Point(x, y), IsClosed = false };
                    geometry.Figures.Add(figure);

                    // M 后面紧跟的坐标对按 LineTo 处理
                    command = relative ? 'l' : 'L';
                    previous = 'M';
                    break;
                }

                case 'L':
                {
                    while (HasNumber())
                    {
                        var x = ReadNumber();
                        var y = ReadNumber();
                        if (relative)
                        {
                            x += curX;
                            y += curY;
                        }

                        curX = x;
                        curY = y;
                        Figure().Segments.Add(new LineSegment { Point = new Point(x, y) });
                    }

                    previous = command;
                    break;
                }

                case 'H':
                {
                    while (HasNumber())
                    {
                        var x = ReadNumber();
                        if (relative)
                        {
                            x += curX;
                        }

                        curX = x;
                        Figure().Segments.Add(new LineSegment { Point = new Point(curX, curY) });
                    }

                    previous = command;
                    break;
                }

                case 'V':
                {
                    while (HasNumber())
                    {
                        var y = ReadNumber();
                        if (relative)
                        {
                            y += curY;
                        }

                        curY = y;
                        Figure().Segments.Add(new LineSegment { Point = new Point(curX, curY) });
                    }

                    previous = command;
                    break;
                }

                case 'C':
                {
                    while (HasNumber())
                    {
                        var x1 = ReadNumber();
                        var y1 = ReadNumber();
                        var x2 = ReadNumber();
                        var y2 = ReadNumber();
                        var x = ReadNumber();
                        var y = ReadNumber();
                        if (relative)
                        {
                            x1 += curX;
                            y1 += curY;
                            x2 += curX;
                            y2 += curY;
                            x += curX;
                            y += curY;
                        }

                        ctrlX = x2;
                        ctrlY = y2;
                        curX = x;
                        curY = y;
                        Figure().Segments.Add(new BezierSegment
                        {
                            Point1 = new Point(x1, y1),
                            Point2 = new Point(x2, y2),
                            Point3 = new Point(x, y),
                        });
                    }

                    previous = command;
                    break;
                }

                case 'S':
                {
                    while (HasNumber())
                    {
                        // 首个控制点 = 上一段第二控制点关于当前点的镜像
                        var reflect = previous is 'C' or 'c' or 'S' or 's';
                        var x1 = reflect ? 2 * curX - ctrlX : curX;
                        var y1 = reflect ? 2 * curY - ctrlY : curY;

                        var x2 = ReadNumber();
                        var y2 = ReadNumber();
                        var x = ReadNumber();
                        var y = ReadNumber();
                        if (relative)
                        {
                            x2 += curX;
                            y2 += curY;
                            x += curX;
                            y += curY;
                        }

                        ctrlX = x2;
                        ctrlY = y2;
                        curX = x;
                        curY = y;
                        Figure().Segments.Add(new BezierSegment
                        {
                            Point1 = new Point(x1, y1),
                            Point2 = new Point(x2, y2),
                            Point3 = new Point(x, y),
                        });
                    }

                    previous = command;
                    break;
                }

                case 'Q':
                {
                    while (HasNumber())
                    {
                        var cx = ReadNumber();
                        var cy = ReadNumber();
                        var x = ReadNumber();
                        var y = ReadNumber();
                        if (relative)
                        {
                            cx += curX;
                            cy += curY;
                            x += curX;
                            y += curY;
                        }

                        ctrlX = cx;
                        ctrlY = cy;
                        curX = x;
                        curY = y;
                        Figure().Segments.Add(new QuadraticBezierSegment
                        {
                            Point1 = new Point(cx, cy),
                            Point2 = new Point(x, y),
                        });
                    }

                    previous = command;
                    break;
                }

                case 'T':
                {
                    while (HasNumber())
                    {
                        var reflect = previous is 'Q' or 'q' or 'T' or 't';
                        var cx = reflect ? 2 * curX - ctrlX : curX;
                        var cy = reflect ? 2 * curY - ctrlY : curY;

                        var x = ReadNumber();
                        var y = ReadNumber();
                        if (relative)
                        {
                            x += curX;
                            y += curY;
                        }

                        ctrlX = cx;
                        ctrlY = cy;
                        curX = x;
                        curY = y;
                        Figure().Segments.Add(new QuadraticBezierSegment
                        {
                            Point1 = new Point(cx, cy),
                            Point2 = new Point(x, y),
                        });
                    }

                    previous = command;
                    break;
                }

                case 'A':
                {
                    while (HasNumber())
                    {
                        var rx = ReadNumber();
                        var ry = ReadNumber();
                        var rotation = ReadNumber();
                        var largeArc = ReadNumber() != 0;
                        var sweep = ReadNumber() != 0;
                        var x = ReadNumber();
                        var y = ReadNumber();
                        if (relative)
                        {
                            x += curX;
                            y += curY;
                        }

                        // SVG 允许半径为 0，此时按直线处理
                        if (rx <= 0 || ry <= 0)
                        {
                            curX = x;
                            curY = y;
                            Figure().Segments.Add(new LineSegment { Point = new Point(x, y) });
                            continue;
                        }

                        curX = x;
                        curY = y;
                        Figure().Segments.Add(new ArcSegment
                        {
                            Size = new Size(rx, ry),
                            RotationAngle = rotation,
                            IsLargeArc = largeArc,
                            SweepDirection = sweep
                                ? SweepDirection.Clockwise
                                : SweepDirection.Counterclockwise,
                            Point = new Point(x, y),
                        });
                    }

                    previous = command;
                    break;
                }

                case 'Z':
                    // 前面已在读到 Z 时处理过，这里兜底
                    if (figure is not null)
                    {
                        figure.IsClosed = true;
                    }

                    curX = startX;
                    curY = startY;
                    previous = command;
                    break;

                default:
                    // 未知命令：保留已解析部分，别整条丢掉
                    return geometry;
            }
        }

        return geometry;
    }

    /// <summary>
    /// 把路径字符串拆成「命令字符 / 数字」混合 token。
    /// 兼容 "M 1,2 L 3 4"、"M1,2L3,4"、"M1-2"（负号直接跟数字）等写法。
    /// </summary>
    internal static List<object> TokenizePath(string data)
    {
        var tokens = new List<object>(data.Length / 2);
        var i = 0;

        while (i < data.Length)
        {
            var c = data[i];

            if (char.IsWhiteSpace(c) || c == ',')
            {
                i++;
                continue;
            }

            if (char.IsLetter(c))
            {
                tokens.Add(c);
                i++;
                continue;
            }

            var start = i;
            if (c is '-' or '+')
            {
                i++;
            }

            while (i < data.Length && (char.IsDigit(data[i]) || data[i] == '.'))
            {
                i++;
            }

            // 科学计数法：1e-3
            if (i < data.Length && data[i] is 'e' or 'E')
            {
                i++;
                if (i < data.Length && data[i] is '-' or '+')
                {
                    i++;
                }

                while (i < data.Length && char.IsDigit(data[i]))
                {
                    i++;
                }
            }

            if (i == start)
            {
                // 无法识别的字符，跳过，避免死循环
                i++;
                continue;
            }

            if (double.TryParse(
                    data.AsSpan(start, i - start),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var value))
            {
                tokens.Add(value);
            }
        }

        return tokens;
    }

    /// <summary>
    /// 富文本：<c>richText</c> 接受 span 数组，每个元素
    /// <c>{ text, bold, italic, underline, size, color }</c>，逐段生成带格式的 Run。
    ///
    /// 与 <c>text</c> 的关系：<c>text</c> 仍是纯文本快捷写法（整段一个 Run），
    /// 需要混排格式时用 <c>richText</c>。两者互不干扰，后应用的覆盖先应用的。
    /// </summary>
    private static bool ApplyRichText(FrameworkElement e, JsonNode? v, Action<string> warn)
    {
        if (v is not JsonArray spans)
        {
            warn("richText 需要一个 span 数组");
            return true;
        }

        if (e is RichTextBlock rich)
        {
            rich.Blocks.Clear();
            var paragraph = new Paragraph();
            foreach (var node in spans)
            {
                if (node is not JsonObject span)
                {
                    // 裸字符串按无格式片段处理，允许 ["普通", {"text":"粗","bold":true}] 混排
                    if (ValueParsers.AsString(node) is { } bare)
                    {
                        paragraph.Inlines.Add(new Run { Text = bare });
                    }

                    continue;
                }

                var run = new Run { Text = ValueParsers.AsString(span["text"]) ?? "" };
                if (ValueParsers.AsBool(span["bold"]) == true)
                {
                    run.FontWeight = FontWeights.Bold;
                }

                if (ValueParsers.AsBool(span["italic"]) == true)
                {
                    run.FontStyle = Windows.UI.Text.FontStyle.Italic;
                }

                if (ValueParsers.AsBool(span["underline"]) == true)
                {
                    run.TextDecorations = Windows.UI.Text.TextDecorations.Underline;
                }

                if (ValueParsers.AsDouble(span["size"]) is { } size && size > 0)
                {
                    run.FontSize = size;
                }

                if (ValueParsers.AsColor(span["color"]) is { } color)
                {
                    run.Foreground = new SolidColorBrush(color);
                }

                paragraph.Inlines.Add(run);
            }

            rich.Blocks.Add(paragraph);
            return true;
        }

        if (e is RichEditBox edit)
        {
            // RichEditBox 的富文本要 ITextRange 逐段设 CharacterFormat，
            // 且与用户编辑的撤销栈交织，容易把撤销弄乱。先按纯文本降级。
            warn("RichEditBox 暂不支持 richText，已按纯文本处理");
            var plain = string.Concat(spans.Select(n =>
                n is JsonObject o ? ValueParsers.AsString(o["text"]) ?? "" : ValueParsers.AsString(n) ?? ""));
            edit.Document.SetText(Microsoft.UI.Text.TextSetOptions.None, plain);
            return true;
        }

        return false;
    }

    private static bool ApplyExtendedText(FrameworkElement e, JsonNode? v, Action<string> warn)
    {
        var text = ValueParsers.AsString(v) ?? "";
        switch (e)
        {
            case RichTextBlock rich:
                rich.Blocks.Clear();
                var paragraph = new Microsoft.UI.Xaml.Documents.Paragraph();
                paragraph.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = text });
                rich.Blocks.Add(paragraph);
                return true;

            case RichEditBox edit:
                edit.Document.SetText(Microsoft.UI.Text.TextSetOptions.None, text);
                return true;

            case AutoSuggestBox suggest:
                suggest.Text = text;
                return true;

            case InfoBar info:
                info.Message = text;
                return true;

            case DropDownButton drop:
                drop.Content = text;
                return true;

            case ToggleSplitButton toggleSplit:
                toggleSplit.Content = text;
                return true;

            case NumberBox number:
                // 允许用 text 传数字字符串，方便从 Entry 抄值
                if (double.TryParse(text, out var parsed))
                    number.Value = parsed;
                else
                    warn($"NumberBox text 不是数字: {text}");
                return true;

            default:
                return false;
        }
    }

    private static bool ApplyExtendedValue(FrameworkElement e, JsonNode? v, Action<string> warn)
    {
        if (ValueParsers.AsDouble(v) is not { } number)
        {
            return false;
        }

        switch (e)
        {
            case NumberBox box:
                box.Value = number;
                return true;
            case RatingControl rating:
                rating.Value = number;
                return true;
            default:
                return false;
        }
    }

    private static bool ApplyExtendedHeader(FrameworkElement e, JsonNode? v)
    {
        var header = ValueParsers.AsString(v) ?? "";
        switch (e)
        {
            case Expander expander: expander.Header = header; return true;
            case NumberBox number: number.Header = header; return true;
            case DatePicker date: date.Header = header; return true;
            case TimePicker time: time.Header = header; return true;
            case CalendarDatePicker calendar: calendar.Header = header; return true;
            case ComboBox combo: combo.Header = header; return true;
            case TextBox textBox: textBox.Header = header; return true;
            case PasswordBox password: password.Header = header; return true;
            case AutoSuggestBox suggest: suggest.Header = header; return true;
            default: return false;
        }
    }

    private static bool ApplyExtendedPlaceholder(FrameworkElement e, JsonNode? v)
    {
        var text = ValueParsers.AsString(v) ?? "";
        switch (e)
        {
            case AutoSuggestBox suggest: suggest.PlaceholderText = text; return true;
            case RichEditBox rich: rich.PlaceholderText = text; return true;
            case NumberBox number: number.PlaceholderText = text; return true;
            default: return false;
        }
    }

    private static bool ApplyExtendedSelectedIndex(FrameworkElement e, JsonNode? v)
    {
        if (ValueParsers.AsInt(v) is not { } index)
        {
            return false;
        }

        switch (e)
        {
            case Pivot pivot:
                pivot.SelectedIndex = index;
                return true;
            case RadioButtons radios:
                radios.SelectedIndex = index;
                return true;
            case SelectorBar bar:
                if (index >= 0 && index < bar.Items.Count)
                    bar.SelectedItem = bar.Items[index];
                return true;
            case NavigationView nav:
                if (index >= 0 && index < nav.MenuItems.Count)
                    nav.SelectedItem = nav.MenuItems[index];
                return true;
            case TabView tabs:
                if (index >= 0 && index < tabs.TabItems.Count)
                    tabs.SelectedItem = tabs.TabItems[index];
                return true;
            // GridView / FlipView 都是 Selector，但原有 ComboBox / ListView
            // 的 selectedIndex 仍归主 switch，别在这里截走。
            case Selector selector when selector is not ComboBox and not ListView:
                selector.SelectedIndex = index;
                return true;
            default:
                return false;
        }
    }

    // ------------------------------------------------------------------
    // items：不同控件的条目类型完全不同，集中在这里构造
    // ------------------------------------------------------------------

    private static bool ApplyExtendedItems(FrameworkElement e, JsonArray array, Action<string> warn)
    {
        var labels = new List<string>(array.Count);
        foreach (var node in array)
        {
            labels.Add(node is JsonObject obj
                ? ValueParsers.AsString(obj["label"]) ?? ValueParsers.AsString(node) ?? ""
                : ValueParsers.AsString(node) ?? "");
        }

        switch (e)
        {
            case GridView grid:
                grid.ItemsSource = labels;
                return true;
            case FlipView flip:
                flip.ItemsSource = labels;
                return true;
            // ListView / ComboBox 的 items 由原 switch 处理（支持带图标的富项），
            // 这里刻意不接管。
            case AutoSuggestBox suggest:
                suggest.ItemsSource = labels;
                return true;
            case BreadcrumbBar breadcrumb:
                breadcrumb.ItemsSource = labels;
                return true;
            case ItemsView itemsView:
                itemsView.ItemsSource = labels;
                return true;

            case Pivot pivot:
                pivot.Items.Clear();
                foreach (var label in labels)
                    pivot.Items.Add(new PivotItem { Header = label, Content = new TextBlock { Text = label } });
                return true;

            case TabView tabs:
                tabs.TabItems.Clear();
                foreach (var label in labels)
                {
                    // items 快捷写法生成的是「可移动、可关闭」的普通标签页；
                    // 需要放子控件时改用 TabPage 子节点。
                    var tab = new TabViewItem
                    {
                        Tag = label,
                        IsClosable = true,
                    };
                    ExtendedControls.SetTabHeader(tab, label);
                    tabs.TabItems.Add(tab);
                }

                return true;

            case NavigationView nav:
                nav.MenuItems.Clear();
                foreach (var label in labels)
                    nav.MenuItems.Add(new NavigationViewItem { Content = label, Tag = label });
                return true;

            case MenuBar menuBar:
                menuBar.Items.Clear();
                foreach (var label in labels)
                {
                    var item = new MenuBarItem { Title = label, Tag = label };
                    item.Items.Add(new MenuFlyoutItem { Text = label, Tag = label });
                    menuBar.Items.Add(item);
                }
                return true;

            case CommandBar commandBar:
                commandBar.PrimaryCommands.Clear();
                foreach (var label in labels)
                    commandBar.PrimaryCommands.Add(new AppBarButton { Label = label, Tag = label });
                return true;

            case RadioButtons radios:
                radios.Items.Clear();
                foreach (var label in labels)
                    radios.Items.Add(label);
                return true;

            case SelectorBar selectorBar:
                selectorBar.Items.Clear();
                foreach (var label in labels)
                    selectorBar.Items.Add(new SelectorBarItem { Text = label, Tag = label });
                return true;

            case TreeView tree:
                tree.RootNodes.Clear();
                BuildTreeNodes(tree.RootNodes, array);
                return true;

            default:
                return false;
        }
    }

    private static void BuildTreeNodes(IList<TreeViewNode> target, JsonArray array)
    {
        foreach (var node in array)
        {
            string label;
            JsonArray? children = null;
            if (node is JsonObject obj)
            {
                label = ValueParsers.AsString(obj["label"]) ?? "";
                children = obj["children"] as JsonArray;
            }
            else
            {
                label = ValueParsers.AsString(node) ?? "";
            }

            var treeNode = new TreeViewNode { Content = label, IsExpanded = true };
            if (children is not null)
            {
                BuildTreeNodes(treeNode.Children, children);
            }

            target.Add(treeNode);
        }
    }

    // ------------------------------------------------------------------
    // 日期 / 时间 / 滚动条
    // ------------------------------------------------------------------

    private static bool ApplyDate(FrameworkElement e, JsonNode? v, Action<string> warn)
    {
        var raw = ValueParsers.AsString(v);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        if (!DateTimeOffset.TryParse(raw, out var date))
        {
            warn($"date 不是可解析的日期: {raw}");
            return true;
        }

        switch (e)
        {
            case DatePicker picker:
                picker.SelectedDate = date;
                break;
            case CalendarDatePicker calendar:
                calendar.Date = date;
                break;
            case CalendarView view:
                view.SelectedDates.Clear();
                view.SelectedDates.Add(date);
                view.SetDisplayDate(date);
                break;
        }

        return true;
    }

    private static bool ApplyTime(FrameworkElement e, JsonNode? v, Action<string> warn)
    {
        var raw = ValueParsers.AsString(v);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        if (!TimeSpan.TryParse(raw, out var time))
        {
            warn($"time 不是可解析的时间: {raw}");
            return true;
        }

        if (e is TimePicker picker)
        {
            picker.SelectedTime = time;
        }

        return true;
    }

    private static ScrollBarVisibility ParseScrollBarVisibility(string mode) =>
        mode.ToLowerInvariant() switch
        {
            "visible" => ScrollBarVisibility.Visible,
            "hidden" => ScrollBarVisibility.Hidden,
            "disabled" => ScrollBarVisibility.Disabled,
            _ => ScrollBarVisibility.Auto,
        };
}
