using System.Numerics;
using System.Runtime.InteropServices;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;
using Microsoft.UI.Composition;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using WinRT.Interop;
using PyWinUI.Host.Interop;
using PyWinUI.Host.Protocol;
using Windows.Graphics;
using Windows.UI;

namespace PyWinUI.Host.Rendering;

/// <summary>
/// 表单渲染器：持有节点表，负责全量建树与增量补丁。
///
/// 节点表的键是协议里的稳定 id。所有 patch 操作都靠它定位控件，
/// 所以 id 的唯一性由 Python 侧保证，这里只做防御性校验。
/// </summary>
internal sealed class FormRenderer
{
    private readonly Window _window;
    private readonly PropertyApplier _applier;
    private readonly EventBridge _bridge;
    private readonly Action<string> _warn;
    private readonly Action<string, string, string?> _error;
    private readonly Action<string, string, JsonObject?, bool> _emit;

    private readonly Dictionary<string, FrameworkElement> _nodes = new();
    private readonly Dictionary<string, NodeSpec> _specs = new();
    private readonly Dictionary<string, string> _parentOf = new();

    private string? _pendingTheme;

    /// <summary>当前根节点 id。窗口级事件按它识别「这条事件属于窗口」。</summary>
    private string? _rootId;

    public FormRenderer(
        Window window,
        Action<string, string, JsonObject?, bool> emit,
        Action<string> warn,
        Action<string, string, string?> error)
    {
        _window = window;
        _warn = warn;
        _error = error;
        _emit = emit;
        _applier = new PropertyApplier(warn, emit, CreateItemChild, ResetItemChildren);
        _bridge = new EventBridge(emit, warn, TryGetNodeId, OwnerOrNull);
    }

    public int NodeCount => _nodes.Count;

    /// <summary>
    /// 每个列表（按父节点 id）在上一轮 items 里创建过的子节点 id。
    /// 列表刷新时据此把旧子节点从节点表摘掉，避免只增不减。
    /// </summary>
    private readonly Dictionary<string, List<string>> _itemChildIds = new();

    /// <summary>
    /// 为 <c>items[].children</c> 创建子控件并注册到节点表。
    ///
    /// id 形如 <c>{列表id}#{条目序号}.{子控件序号}</c>：稳定、可预测，
    /// 事件回传时能按元素反查到它。props 里缺省时给空对象，
    /// 让 NodeFactory 走默认值而不是拿到 null。
    /// </summary>
    private FrameworkElement? CreateItemChild(string parentId, int itemIndex, int childIndex, JsonObject spec)
    {
        var type = ValueParsers.AsString(spec["type"]);
        if (string.IsNullOrWhiteSpace(type))
        {
            _warn($"items[{itemIndex}].children[{childIndex}] 缺少 type");
            return null;
        }

        if (string.IsNullOrEmpty(parentId))
        {
            _warn("items.children 无法定位父节点 id，已跳过");
            return null;
        }

        var id = $"{parentId}#{itemIndex}.{childIndex}";
        var props = spec["props"] as JsonObject ?? new JsonObject();

        FrameworkElement element;
        try
        {
            element = NodeFactory.Create(type, id, props, _warn);
        }
        catch (Exception ex)
        {
            _warn($"items.children 创建 {type} 失败: {ex.Message}");
            return null;
        }

        _nodes[id] = element;

        if (!_itemChildIds.TryGetValue(parentId, out var ids))
        {
            ids = new List<string>();
            _itemChildIds[parentId] = ids;
        }

        ids.Add(id);
        return element;
    }

    /// <summary>列表重建前，把上一轮该列表创建的子节点从节点表移除。</summary>
    private void ResetItemChildren(string parentId)
    {
        if (string.IsNullOrEmpty(parentId) || !_itemChildIds.Remove(parentId, out var ids))
        {
            return;
        }

        foreach (var id in ids)
        {
            _nodes.Remove(id);
            _specs.Remove(id);
            _parentOf.Remove(id);
        }
    }

    /// <summary>某个节点是否真的订阅了某个事件（select_tab 用它避免重复回事件）。</summary>
    public bool IsEventBound(string nodeId, string eventName) =>
        _bridge.IsBound(nodeId, eventName);

    private FrameworkElement? OwnerOrNull(string nodeId) =>
        TryGetOwner(nodeId, out var owner) ? owner : null;

    public bool TryGetElement(string id, [NotNullWhen(true)] out FrameworkElement? element) =>
        _nodes.TryGetValue(id, out element);

    /// <summary>
    /// 取某个节点的直接父元素（协议层的父，不是可视树的父）。
    /// TabViewItem 放在 TabItems 里时 <c>Parent</c> 是 null，未选中的页
    /// 往往还没实体化，沿可视树往上找并不可靠。
    /// </summary>
    public bool TryGetOwner(string nodeId, out FrameworkElement owner)
    {
        if (_parentOf.TryGetValue(nodeId, out var parentId)
            && _nodes.TryGetValue(parentId, out var element))
        {
            owner = element;
            return true;
        }

        owner = null!;
        return false;
    }

    /// <summary>
    /// 把元素反查成节点 id。事件载荷需要指向「具体某个子元素」时用，
    /// 例如用户关闭的是哪个标签页 —— 只回标题文字的话，同名标签会关错。
    /// </summary>
    public string? TryGetNodeId(DependencyObject element)
    {
        foreach (var pair in _nodes)
        {
            if (ReferenceEquals(pair.Value, element))
            {
                return pair.Key;
            }
        }

        return null;
    }

    // ==================================================================
    // 全量渲染
    // ==================================================================

    /// <summary>
    /// 全量渲染。
    /// </summary>
    /// <returns>
    /// 成功返回 true。失败时会把加载圈换成错误提示并返回 false，
    /// 调用方据此决定要不要把窗口装饰（最大化/最小化）交还给用户。
    /// </returns>
    public bool Render(RenderMessage message)
    {
        HostLog.Write($"[render] start nodes={message.Nodes.Count} root={message.Root}");

        Reset();
        HostLog.Write("[render] reset ok");

        _pendingTheme = message.Window.Theme;
        _rootId = message.Root;

        // 注意：这里刻意先不碰窗口尺寸和内容。
        // 建树要几百毫秒，如果先把窗口放大，用户会看到「窗口变大但内容还是加载圈」。
        // 尺寸调整放到最后，和切换 Content 紧挨着，让 WinUI 在同一帧里一起处理。

        // 第一步：把所有节点创建出来。
        // 必须早于任何父子关系操作，因为 children 可以引用后面才定义的节点。
        foreach (var spec in message.Nodes)
        {
            if (string.IsNullOrEmpty(spec.Id))
            {
                _error("bad-protocol", "节点缺少 id", null);
                continue;
            }

            if (_nodes.ContainsKey(spec.Id))
            {
                _error("bad-protocol", $"节点 id 重复: {spec.Id}", spec.Id);
                continue;
            }

            var created = NodeFactory.Create(spec.Type, spec.Id, spec.Props, _warn);
            _nodes[spec.Id] = created;
            _specs[spec.Id] = spec;
            if (created is WebView2 webNode)
            {
                WebView2Support.Attach(webNode, spec.Id, _emit, _warn);
            }
        }

        HostLog.Write($"[render] created {_nodes.Count} nodes");

        // 第二步：应用属性。
        // 放在组装之前，这样 Grid 的行列定义已经就绪，子元素加入时布局才正确。
        foreach (var spec in message.Nodes)
        {
            if (!_nodes.TryGetValue(spec.Id, out var element))
            {
                continue;
            }

            ApplyPropsSuppressed(spec.Id, element, spec.Props);
        }

        HostLog.Write("[render] props ok");

        // 第三步：组装父子关系。
        foreach (var spec in message.Nodes)
        {
            if (spec.Children is null || spec.Children.Count == 0)
            {
                continue;
            }

            if (!_nodes.TryGetValue(spec.Id, out var parentElement))
            {
                continue;
            }

            if (!CanHaveChildren(parentElement))
            {
                _error("bad-protocol", $"节点 {spec.Id}（{spec.Type}）不能包含子节点", spec.Id);
                continue;
            }

            // TabView 特殊：items 生成的普通标签页此时已经在 TabItems 里，
            // 不能 Clear（会把它们冲掉）；子节点只追加，顺序由标签条自己管理。
            // 其它容器是全新树，先清空再按 children 顺序挂。
            if (parentElement is not TabView && ChildHostOf(parentElement) is { } panel)
            {
                panel.Children.Clear();
            }

            var position = 0;
            foreach (var childId in spec.Children)
            {
                if (!_nodes.TryGetValue(childId, out var childElement))
                {
                    _error("unknown-node", $"子节点 {childId} 不存在", spec.Id);
                    continue;
                }

                // 同一个控件不能被挂到两个父容器下（WinUI 会直接抛异常终止进程），
                // 这里提前拦下来，退化成一个可报告的协议错误
                if (_parentOf.TryGetValue(childId, out var existingParent))
                {
                    _error(
                        "bad-protocol",
                        $"子节点 {childId} 已挂在 {existingParent} 下，不能重复挂载",
                        childId);
                    continue;
                }

                if (childElement.Parent is not null)
                {
                    _error("bad-protocol", $"子节点 {childId} 已有父容器", childId);
                    continue;
                }

                if (!AttachChild(parentElement, childElement, position++))
                {
                    _error("bad-protocol", $"节点 {childId} 不能作为 {spec.Id} 的子节点", childId);
                    continue;
                }

                _parentOf[childId] = spec.Id;
            }
        }

        HostLog.Write("[render] tree ok");

        // 第四步：挂事件。放在最后，避免组装过程中的中间状态触发回调。
        foreach (var spec in message.Nodes)
        {
            if (!_nodes.TryGetValue(spec.Id, out var element))
            {
                continue;
            }

            if (spec.Events is { Count: > 0 })
            {
                BindEvents(spec.Id, element, spec.Events.Keys);
            }
        }

        HostLog.Write("[render] events ok");

        // 第五步：挂到窗口上
        if (!_nodes.TryGetValue(message.Root, out var root))
        {
            _error("unknown-node", $"根节点 {message.Root} 不存在", message.Root);
            ShowErrorView($"根节点 {message.Root} 不存在");
            return false;
        }

        if (_parentOf.ContainsKey(message.Root))
        {
            _error("bad-protocol", $"根节点 {message.Root} 不能有父节点", message.Root);
            ShowErrorView("根节点不能有父节点");
            return false;
        }

        // 最后一步：先调窗口尺寸，紧接着换内容。
        // 两步之间不放任何耗时操作，这样窗口放大和内容出现落在同一帧，
        // 不会出现「先变大再出现控件」的割裂感。
        ApplyWindowSpec(message.Window);

        // 整窗滚动默认开启。包在这一层是刻意的：只有窗口内容区的高度是有界的，
        // 放进 StackPanel 里的 ScrollViewer 会被测量成无限高，永远不滚。
        var content = message.Window.Scroll == false ? root : WrapForScroll(root);

        // 所有窗口共用同一个外壳（自绘标题栏 + 内容区），这里只替换内容区。
        // 标题栏不跟着控件树重建，否则每次 render 都会把拖动/悬停状态重置掉。
        EnsureShell();
        _contentHost!.Children.Clear();
        _contentHost.Children.Add(content);
        HostLog.Write("[render] window sized + content swapped");

        ApplyTheme(_pendingTheme);
        HostLog.Write("[render] done");
        return true;
    }

    /// <summary>标题栏高度（DIP）。有返回按钮或输入框时加高，给部件留出空间。</summary>
    private const int CompactTitleBarHeight = WindowChrome.TitleBarHeight;
    private const int ExtendedTitleBarHeight = 46;

    private int TitleBarHeight =>
        (_returnButton || _textInputBox) ? ExtendedTitleBarHeight : CompactTitleBarHeight;

    /// <summary>标题栏输入框的字号与高度。上下 padding 由这两个值算出。</summary>
    private const double TitleInputFontSize = 12;
    private const double TitleInputHeight = 32;

    /// <summary>窗口圆角半径（DIP）。</summary>
    /// <summary>自绘窗口圆角半径（父窗口、子窗口共用）。</summary>
    private const int WindowCornerRadius = 8;

    /// <summary>窗口外围那圈完全透明的边（宽多少像素）。</summary>
    private const int WindowTransparentMargin = WindowChrome.TransparentMargin;

    /// <summary>
    /// 建好窗口外壳（自绘标题栏 + 内容区），只建一次。
    ///
    /// 用常驻外壳而不是每次 render 重新包一层，是因为标题栏要保留状态：
    /// 拖动中、按钮悬停、最大化图标，这些都不该被一次内容刷新打断。
    /// </summary>
    private void EnsureShell()
    {
        if (_shell is not null)
        {
            return;
        }

        _contentHost = new Grid();
        var bar = BuildTitleBar();

        var shell = new Grid();
        shell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        shell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(bar, 0);
        Grid.SetRow(_contentHost, 1);
        shell.Children.Add(bar);
        shell.Children.Add(_contentHost);

        // 内容容器：白底 + 圆角 + 1px 外框线。
        var inner = new Border
        {
            Background = new SolidColorBrush(ContentBackground()),

            // 这 1px 就是窗口的外框线。
            //
            // 两点注意：
            // - BorderThickness 不能给 0。WinUI 的 Border 是按 border geometry
            //   画圆角边框的，厚度为 0 会退化成直角（实测外框线整个消失）。
            // - 这条线跟着 CornerRadius 走，是圆角的，不会变成方形轮廓。
            BorderBrush = new SolidColorBrush(FrameColor()),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(CornerRadiusForWindow()),
            Child = shell,
        };

        // 外围一圈**完全透明**的边。
        //
        // 这里只画 Padding、不画任何底色：那一圈区域没有任何像素，
        // 露出的是 DWM 玻璃层后面的桌面——也就是真正的「透明边框」。
        // 内容区（inner）的尺寸不受影响，只是整体往里挪。
        //
        // 这一层同时把外壳「降」成 Window.Content 的子元素：
        // 要裁剪圆角只能靠 Composition 的 GeometricClip，而 Clip 设在
        // Window.Content 上是无效的（那是框架托管的 island 根 visual，
        // 改动会被框架忽略），所以被裁的 inner 必须是普通元素。
        var root = new Border
        {
            Background = null,
            Padding = new Thickness(WindowTransparentMargin),
            Child = inner,
        };

        _shell = shell;
        _outerFrame = root;
        _innerFrame = inner;
        _window.Content = root;

        // 窗口状态（最大化 / 嵌入）变了就要重算外观
        UpdateChrome();

        // AppWindow 对**嵌入子窗口**是 null——它已经被 SetParent 改成 WS_CHILD，
        // WinUI 的 AppWindow 随之失效。这里必须判空，否则子窗口一渲染就
        // NullReferenceException。
        var appWindow = _window.AppWindow;
        if (appWindow is not null)
        {
            appWindow.Changed += (_, _) => UpdateChrome();
        }

        // Border.CornerRadius 只影响它自己背景/边框的绘制，**不会裁剪子元素**。
        // 客户区铺满整个窗口之后，子元素（内容区的白底、滚动层）会越过圆角
        // 一直画到窗口四角外面，让"透明圆角"看起来始终有一圈浅色。
        // 这里用 Composition 的几何裁剪把整棵子树真正裁到圆角内。
        if (inner.IsLoaded)
        {
            AttachRoundedClip(inner);
        }
        else
        {
            inner.Loaded += (_, _) => AttachRoundedClip(inner);
        }

        // 标题栏这会儿才建好，把之前收到的状态补上
        SetMaximizeRequestable(_maximizeRequestable);
        UpdateInnerBackground();

        // 从窗口属性读实际图标，替换标题栏里的占位字形
        _ = RefreshTitleIconAsync();
    }

    /// <summary>
    /// 从 Win32 窗口属性读实际图标，填进自绘标题栏。
    ///
    /// AppWindow.SetIcon 是异步落地的，设置完立刻读可能还是旧图标；
    /// 所以 ApplyWindowIcon 里也会再调一次来覆盖。
    /// </summary>
    private async Task RefreshTitleIconAsync()
    {
        if (_titleIcon is null)
        {
            return;
        }

        var hwnd = WindowNative.GetWindowHandle(_window);
        var source = await WindowIcon.LoadAsync(hwnd, size: 16);
        if (source is null)
        {
            return;
        }

        var icon = _titleIcon;
        icon.Source = source;
    }

    /// <summary>
    /// 按当前窗口状态重算外观。
    ///
    /// 两条规则：
    /// - **嵌入的子窗口**：不要外围透明边。
    /// - **最大化**：不要外围透明边，也不要圆角——最大化时窗口就是整块屏幕，
    ///   再留一圈透明边和圆角会露出屏幕边缘的一线，很怪。
    /// </summary>
    private void UpdateChrome()
    {
        if (_outerFrame is null || _innerFrame is null)
        {
            return;
        }

        var maximized = IsMaximizedNative();

        var margin = (_embedded || maximized) ? 0 : WindowTransparentMargin;
        _outerFrame.Padding = new Thickness(margin);

        var radius = CornerRadiusForWindow();
        _innerFrame.CornerRadius = new CornerRadius(radius);

        if (_clipGeometry is not null)
        {
            _clipGeometry.CornerRadius = new Vector2(radius, radius);
        }

        // 标题栏的上两角圆角必须和内容容器用同一个半径：
        // - 最大化：都为 0，直角铺满屏幕；
        // - Win11 顶层：都为 0，圆角交给 DWM，自绘标题栏不能再画一层，
        //   否则系统圆角和自绘圆角叠成双重轮廓；
        // - Win10 / 子窗口：都用 8px 自绘圆角。
        if (_titleBarBorder is not null)
        {
            _titleBarBorder.CornerRadius = maximized
                ? new CornerRadius(0)
                : new CornerRadius(radius, radius, 0, 0);
        }

        // 最大化按钮的图标也在这里统一管。
        //
        // 之前靠 Session 在切换最大化时调 SetMaximizedVisual，但**拖动标题栏
        // 还原**走的是 presenter.Restore()，不经过那条路径，图标就不更新了。
        // 子窗口的按钮是「折叠/填满」语义，不受最大化影响。
        if (_maximizeGlyph is not null && !_embedded)
        {
            _maximizeGlyph.Glyph = maximized ? "\uE923" : "\uE922";
        }
    }

    /// <summary>
    /// 这扇窗口该用多大的自绘圆角。
    ///
    /// - 最大化：0（铺满屏幕，保留圆角会露出屏幕边缘一条缝）。
    /// - Win11 顶层窗口：0 —— 圆角交给 DWM 画（DWMWCP_ROUND），
    ///   它带抗锯齿、阴影也对。自绘的同时会叠成双重轮廓，所以让位。
    /// - 其余（Win10、嵌入子窗口）：自绘 12px。
    ///   子窗口即使在 Win11 上也得自绘——DWM 的窗口圆角只对顶层有效。
    /// </summary>
    private int CornerRadiusForWindow()
    {
        if (IsMaximizedNative())
        {
            return 0;
        }

        if (CustomChrome.CanUseSystemCorner(_embedded))
        {
            return 0;
        }

        return WindowCornerRadius;
    }

    private const int GWL_STYLE = -16;
    private const long WS_MAXIMIZE = 0x01000000L;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    /// <summary>从最大化还原成普通窗口。</summary>
    /// <summary>窗口当前是不是最大化状态。</summary>
    private bool IsMaximizedNative()
    {
        var hwnd = WindowNative.GetWindowHandle(_window);
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        return (GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64() & WS_MAXIMIZE) != 0;
    }

    /// <summary>外框颜色：深色主题下 25% 灰几乎看不见，提到 75% 才有边界感。</summary>
    private static Windows.UI.Color FrameColor()
    {
        var dark = Application.Current.RequestedTheme == ApplicationTheme.Dark;
        return dark
            ? Windows.UI.Color.FromArgb(191, 0x80, 0x80, 0x80)
            : Windows.UI.Color.FromArgb(64, 0x80, 0x80, 0x80);
    }

    /// <summary>内容区底色。窗口透明之后这层必须不透明，否则会露出桌面。</summary>
    private static Windows.UI.Color ContentBackground()
    {
        var dark = Application.Current.RequestedTheme == ApplicationTheme.Dark;
        return dark
            ? Windows.UI.Color.FromArgb(255, 0x20, 0x20, 0x20)
            : Windows.UI.Color.FromArgb(255, 0xFF, 0xFF, 0xFF);
    }

    /// <summary>
    /// 用 Composition 的圆角几何裁剪根元素——这是 Win10 上唯一能抗锯齿的做法。
    ///
    /// SetWindowRgn 是二值掩码，边缘只能是硬边；Composition 几何裁剪按像素覆盖率
    /// 计算 alpha，边缘平滑。前提是窗口背景透明，四角才能露出后面的东西
    /// （透明在 App.xaml.cs / Session.CreateWindow 里设置）。
    /// </summary>
    private void AttachRoundedClip(FrameworkElement target)
    {
        var visual = ElementCompositionPreview.GetElementVisual(target);
        var compositor = visual.Compositor;

        var geometry = compositor.CreateRoundedRectangleGeometry();

        // 和内容容器、标题栏共用同一个半径。Win11 顶层窗口这里是 0：
        // 圆角由 DWM 按整个窗口画，XAML 里再裁一次会变成双重轮廓。
        var radius = CornerRadiusForWindow();
        geometry.CornerRadius = new Vector2(radius, radius);
        geometry.Size = new Vector2(
            (float)Math.Max(1, target.ActualWidth),
            (float)Math.Max(1, target.ActualHeight));

        visual.Clip = compositor.CreateGeometricClip(geometry);
        _clipGeometry = geometry;

        target.SizeChanged += (_, e) =>
        {
            geometry.Size = new Vector2(
                (float)Math.Max(1, e.NewSize.Width),
                (float)Math.Max(1, e.NewSize.Height));
        };
    }

    /// <summary>标题栏：应用图标 + 标题文字 + 最小化 / 最大化 / 关闭。</summary>
    private FrameworkElement BuildTitleBar()
    {
        var glyphColor = Windows.UI.Color.FromArgb(255, 0x1A, 0x1A, 0x1A);
        var whiteColor = Windows.UI.Color.FromArgb(255, 0xFF, 0xFF, 0xFF);
        var clearGray = Windows.UI.Color.FromArgb(0, 0xE5, 0xE5, 0xE5);
        var clearRed = Windows.UI.Color.FromArgb(0, 0xC4, 0x2B, 0x1C);
        var hoverGray = Windows.UI.Color.FromArgb(255, 0xE5, 0xE5, 0xE5);
        var pressedGray = Windows.UI.Color.FromArgb(255, 0xCC, 0xCC, 0xCC);
        var hoverRed = Windows.UI.Color.FromArgb(255, 0xC4, 0x2B, 0x1C);
        var pressedRed = Windows.UI.Color.FromArgb(255, 0xF1, 0x70, 0x7A);

        // ---- 左侧：返回按钮（可选） + 窗口图标 + 标题 + 输入框（可选） ----
        //
        // 图标从 Win32 窗口属性里读（WM_GETICON / 类默认图标），不是写死的
        // Segoe 字形——那样设了 icon= 也只在任务栏生效，标题栏一个样。
        // WindowIcon.LoadAsync 是异步的，先放占位字形，拿到图标再替换。

        // 返回按钮在图标**左边**。和最大化按钮一样，点击不自己改窗口，
        // 只发事件给 Python，由回调决定是返回上一页还是关窗。
        var returnGlyph = new FontIcon
        {
            Glyph = "\uE72B",
            FontSize = 12,
            Foreground = new SolidColorBrush(glyphColor),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var returnBox = CreateBarButton(
            returnGlyph, clearGray, hoverGray, pressedGray,
            () => ReturnButtonPressed?.Invoke());
        // 和右侧按钮同宽：返回按钮贴到标题栏最左边时，左右视觉重量对称。
        returnBox.Width = 46;
        // 右边留 8px，和图标到标题文字的间距一致——
        // 不加的话按钮直接贴着图标，看着挤在一起。
        returnBox.Margin = new Thickness(0, 0, 8, 0);
        returnBox.DoubleTapped += (_, e) => e.Handled = true;
        _returnBox = returnBox;

        var icon = new Microsoft.UI.Xaml.Controls.Image
        {
            Width = 16,
            Height = 16,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
            Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform,
        };
        _titleIcon = icon;

        _titleText = new TextBlock
        {
            Text = _window.Title,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = new SolidColorBrush(glyphColor),
        };

        // 标题右边的输入框：放大镜字形浮在 TextBox 左侧内边距里。
        // 边框画在 Host 的 Border 上，TextBox 自己那圈边框去掉，
        // 否则会叠成两条线、圆角也对不上。
        var inputBorder = new Border
        {
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xE0, 0xE0, 0xE0)),
            Background = new SolidColorBrush(ContentBackground()),
        };
        // 文字实际占的是**行高**，不是字号：pt → px 约 ×1.334，
        // 12 号字就是 16px。内容区按这个留，剩余高度平分给上下。
        // 只按字号扣的话内容区不够高，文字会被裁/贴边。
        const double ptToPx = 1.334;
        var inputVPadding = Math.Max(
            0, (TitleInputHeight - TitleInputFontSize * ptToPx) / 2.0);

        var inputBox = new TextBox
        {
            FontSize = TitleInputFontSize,
            Height = TitleInputHeight,
            MinHeight = 0,
            Margin = new Thickness(0),
            // 左边 30px 留给放大镜图标；上下按字号算，别写死。
            Padding = new Thickness(30, inputVPadding, 8, inputVPadding),
            Background = null,
            BorderThickness = new Thickness(0),

            // 文字显式左对齐、垂直居中。
            // TextBox 默认样式的对齐方式会让文字/光标落在它自己的内容区里，
            // 和外面的 Border 差一截——表现就是「在框里点，光标却跑到别处」。
            TextAlignment = TextAlignment.Left,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            VerticalContentAlignment = VerticalAlignment.Center,

            // 填满 inputHost：边框、TextBox、放大镜三层占据同一格，
            // 不能让 TextBox 自己居中，否则它的可点区域比边框窄一圈。
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        var inputIcon = new FontIcon
        {
            Glyph = "\uE721",
            FontSize = 12,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x8A, 0x8A, 0x8A)),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(9, 0, 0, 0),
            IsHitTestVisible = false,
        };

        // 宽度不写死：放在 Star 列里，随标题栏剩下的空间伸缩。
        // 高度固定 32，在 46 的加高标题栏里垂直居中。
        var inputHost = new Grid
        {
            Height = TitleInputHeight,
            MinWidth = 120,
            Margin = new Thickness(12, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        inputHost.Children.Add(inputBorder);
        inputHost.Children.Add(inputBox);
        inputHost.Children.Add(inputIcon);

        // 输入框里的点击/双击不能被标题栏抢去拖动或最大化
        inputHost.PointerPressed += (_, e) => e.Handled = true;
        inputHost.DoubleTapped += (_, e) => e.Handled = true;

        inputBox.TextChanged += (_, _) => EmitTitleInput("textInputChanged");
        inputBox.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                e.Handled = true;
                EmitTitleInput("textInputSubmitted");
            }
        };
        _titleInputHost = inputHost;
        _titleInput = inputBox;

        // 左侧部件用 Grid 而不是 StackPanel：最后一列是 Star，
        // 输入框才能吃掉标题文字与右侧按钮之间的剩余宽度。
        var left = new Grid
        {
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        left.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        left.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        left.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        left.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        Grid.SetColumn(returnBox, 0);
        Grid.SetColumn(icon, 1);
        Grid.SetColumn(_titleText, 2);
        Grid.SetColumn(inputHost, 3);
        left.Children.Add(returnBox);
        left.Children.Add(icon);
        left.Children.Add(_titleText);
        left.Children.Add(inputHost);

        // ---- 右侧按钮 ----
        // 最小化图标随折叠状态切换：子窗口折叠后显示「还原」
        var minimizeGlyph = new FontIcon
        {
            Glyph = Embedded && EmbeddedCollapsed ? "\uE923" : "\uE921",
            FontSize = 10,
            Foreground = new SolidColorBrush(glyphColor),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _minimizeGlyph = minimizeGlyph;
        var minimizeBox = CreateBarButton(
            minimizeGlyph, clearGray, hoverGray, pressedGray,
            () => MinimizeButtonPressed?.Invoke());
        _minimizeBox = minimizeBox;

        var maximizeGlyph = new FontIcon
        {
            Glyph = "\uE922",
            FontSize = 10,
            Foreground = new SolidColorBrush(glyphColor),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _maximizeGlyph = maximizeGlyph;
        var maximizeBox = CreateBarButton(
            maximizeGlyph, clearGray, hoverGray, pressedGray,
            () =>
            {
                // 没挂 Python 回调之前，最大化按钮是禁用的
                if (_maximizeRequestable)
                {
                    MaximizeButtonPressed?.Invoke();
                }
            });
        _maximizeBox = maximizeBox;

        // 关闭是唯一会变红的：悬停红底白叉
        var closeGlyph = new FontIcon
        {
            Glyph = "\uE8BB",
            FontSize = 10,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var closeBackground = new SolidColorBrush(clearRed);
        var closeGlyphBrush = new SolidColorBrush(glyphColor);
        closeGlyph.Foreground = closeGlyphBrush;

        var closeBox = new Border
        {
            Width = 46,
            Height = TitleBarHeight,
            Background = closeBackground,
            Child = closeGlyph,
        };
        _closeBox = closeBox;
        closeBox.PointerEntered += (_, _) =>
        {
            FadeBrush(closeBackground, hoverRed);
            FadeBrush(closeGlyphBrush, whiteColor);
        };
        closeBox.PointerExited += (_, _) =>
        {
            FadeBrush(closeBackground, clearRed);
            FadeBrush(closeGlyphBrush, glyphColor);
        };
        closeBox.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            FadeBrush(closeBackground, pressedRed);
        };
        closeBox.PointerReleased += (_, e) =>
        {
            e.Handled = true;
            FadeBrush(closeBackground, hoverRed);
            CloseButtonPressed?.Invoke();
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        buttons.Children.Add(minimizeBox);
        buttons.Children.Add(maximizeBox);
        buttons.Children.Add(closeBox);

        // 左边距不写死在这里：有返回按钮时它要贴到标题栏最左边（和右侧
        // 按钮组一样），没返回按钮时图标才保留 12px 缩进。
        // 具体由 ApplyTitleBarOptions 按 _returnButton 决定。
        var inner = new Grid();
        _titleBarInner = inner;
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(left, 0);
        Grid.SetColumn(buttons, 1);
        inner.Children.Add(left);
        inner.Children.Add(buttons);

        var bar = new Border
        {
            Height = TitleBarHeight,
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xFF, 0xFF, 0xFF)),
            CornerRadius = new CornerRadius(WindowCornerRadius, WindowCornerRadius, 0, 0),
            Child = inner,
        };

        // ---- 拖动 ----
        // 手动指针驱动：系统移动循环要求调用时左键已按下，从 PointerPressed 里
        // 喂进去时机总差一拍。捕获指针是为了拖出标题条后仍能继续收到移动事件。
        bar.PointerPressed += (_, e) =>
        {
            e.Handled = true;

            // 最大化状态下拖标题栏 = 先还原再拖，由 BeginDrag 负责
            // （它还原后会把窗口挪到光标位置，避免拖动时跳一下）。

            _draggingTitleBar = true;
            bar.CapturePointer(e.Pointer);

            // 记下指针在标题栏里的位置。从最大化还原时，要把标题栏上
            // 「按下的那个点」对回鼠标底下，所以这个坐标得留一份。
            var point = e.GetCurrentPoint(bar).Position;
            EmbeddedWindow.BeginDrag(_window, point.X, point.Y);
        };
        bar.PointerMoved += (_, _) =>
        {
            if (_draggingTitleBar)
            {
                EmbeddedWindow.UpdateDrag(_window);
            }
        };
        bar.PointerReleased += (_, e) =>
        {
            if (!_draggingTitleBar)
            {
                return;
            }

            _draggingTitleBar = false;
            bar.ReleasePointerCapture(e.Pointer);
            EmbeddedWindow.EndDrag(_window);
        };
        bar.PointerCaptureLost += (_, _) =>
        {
            _draggingTitleBar = false;
            EmbeddedWindow.EndDrag(_window);
        };

        // 双击标题栏 = 最大化 / 还原，和系统标题栏一致
        bar.DoubleTapped += (_, e) =>
        {
            e.Handled = true;

            if (_maximizeRequestable)
            {
                MaximizeButtonPressed?.Invoke();
            }
        };

        _titleBarBorder = bar;

        // 标题栏刚建好，把 render 之前收到的开关补上
        ApplyTitleBarOptions();

        return bar;
    }

    /// <summary>
    /// 按 window spec 显隐标题栏上的返回按钮和输入框。
    ///
    /// 两者始终建好，只切 Visibility——这样 configure() 改属性时不必重建
    /// 标题栏（重建会打断拖动状态和输入框里的文字）。
    /// </summary>
    private void ApplyTitleBarOptions()
    {
        var returnVisibility = _returnButton ? Visibility.Visible : Visibility.Collapsed;
        if (_returnBox is not null && _returnBox.Visibility != returnVisibility)
        {
            _returnBox.Visibility = returnVisibility;
        }

        // 返回按钮要贴到标题栏最左边——和右侧按钮组一样从边缘起算。
        // 没有返回按钮时，图标仍然保留原来的 12px 缩进。
        if (_titleBarInner is not null)
        {
            _titleBarInner.Padding = new Thickness(_returnButton ? 0 : 12, 0, 0, 0);
        }

        var inputVisibility = _textInputBox ? Visibility.Visible : Visibility.Collapsed;
        if (_titleInputHost is not null && _titleInputHost.Visibility != inputVisibility)
        {
            _titleInputHost.Visibility = inputVisibility;
        }

        // 任一扩展部件打开，标题栏从 32 加高到 46：
        // 返回按钮和输入框都比 32 高，挤在默认高度里会顶到上下边缘。
        // 按钮、标题栏本体一起改，否则按钮会浮在加高后的空白里。
        var height = TitleBarHeight;
        if (_titleBarBorder is not null)
        {
            _titleBarBorder.Height = height;
        }

        foreach (var box in new[] { _returnBox, _minimizeBox, _maximizeBox, _closeBox })
        {
            if (box is not null)
            {
                box.Height = height;
            }
        }

        // 子窗口的拖动禁区按父窗口标题栏高度算，得跟着一起更新，
        // 否则加高后子窗口还能被拖到标题栏的按钮上。
        var hwnd = WindowNative.GetWindowHandle(_window);
        if (hwnd != IntPtr.Zero)
        {
            EmbeddedWindow.SetTitleBarHeight(hwnd, height);
        }
    }

    /// <summary>把标题栏输入框里的文本发回 Python。</summary>
    private void EmitTitleInput(string name)
    {
        if (_rootId is null)
        {
            return;
        }

        var data = new JsonObject { ["text"] = _titleInput?.Text ?? "" };
        _emit(_rootId, name, data, false);
    }

    /// <summary>标题栏上的普通按钮（最小化 / 最大化同款）：透明底，悬停浅灰。</summary>
    private Border CreateBarButton(
        FontIcon glyph,
        Windows.UI.Color clear,
        Windows.UI.Color hover,
        Windows.UI.Color pressed,
        Action onClick)
    {
        var background = new SolidColorBrush(clear);
        var box = new Border
        {
            Width = 46,
            Height = TitleBarHeight,
            Background = background,
            Child = glyph,
        };

        box.PointerEntered += (_, _) => FadeBrush(background, hover);
        box.PointerExited += (_, _) => FadeBrush(background, clear);
        box.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            FadeBrush(background, pressed);
        };
        box.PointerReleased += (_, e) =>
        {
            e.Handled = true;
            FadeBrush(background, clear);
            onClick();
        };

        return box;
    }

    /// <summary>
    /// 启用 / 禁用标题栏上的最大化按钮。
    ///
    /// 只有 Python 那边挂了最大化回调才启用——没挂就不给点。
    /// </summary>
    public void SetMaximizeRequestable(bool requestable)
    {
        _maximizeRequestable = requestable;

        if (_maximizeBox is null)
        {
            return;
        }

        _maximizeBox.Opacity = requestable ? 1.0 : 0.3;
        _maximizeBox.IsHitTestVisible = requestable;
    }

    /// <summary>最大化 / 还原状态变化时切图标。</summary>
    public void SetMaximizedVisual(bool maximized)
    {
        if (_maximizeGlyph is not null)
        {
            _maximizeGlyph.Glyph = maximized ? "\uE923" : "\uE922";
        }
    }

    /// <summary>标题文字联动，供 configure(title=...) 之后调用。</summary>
    public void SetTitleVisual(string title)
    {
        if (_titleText is not null)
        {
            _titleText.Text = title;
        }
    }


    /// <summary>
    /// 每个 brush 复用一个 Storyboard。
    ///
    /// 为什么要复用而不是每次新建：
    /// 1. 新建的 Storyboard 只放在局部变量里，方法返回就可能被 GC 回收，
    ///    动画停在半路——看起来就是"淡到一半不动"；
    /// 2. 同一属性上跑着两条 Storyboard 会互相抢写，快速进出时颜色会抖。
    /// 复用一个、每次 Stop 后重设 From/To 再 Begin，两个问题一起没了。
    /// </summary>
    private static readonly Dictionary<SolidColorBrush, Storyboard> BrushAnimations = new();

    /// <summary>
    /// 把 brush 的颜色补间到目标色（默认 200ms，对齐系统标题栏按钮的过渡感）。
    ///
    /// 必须开 <c>EnableDependentAnimation</c>：ColorAnimation 走 UI 线程逐帧计算，
    /// 不开这个开关 WinUI 会静默忽略整条动画——看起来像"没生效"，实际上是被拦掉了。
    ///
    /// <c>From</c> 取 brush 当前颜色：动画没播完就再次触发（悬停没结束就移出），
    /// 会从"此刻屏幕上的颜色"接着补间，而不是跳回起点。
    /// </summary>
    private static void FadeBrush(SolidColorBrush brush, Windows.UI.Color to, int milliseconds = 200)
    {
        if (!BrushAnimations.TryGetValue(brush, out var storyboard))
        {
            var created = new ColorAnimation { EnableDependentAnimation = true };
            Storyboard.SetTarget(created, brush);
            Storyboard.SetTargetProperty(created, "Color");

            storyboard = new Storyboard();
            storyboard.Children.Add(created);
            BrushAnimations[brush] = storyboard;
        }

        var animation = (ColorAnimation)storyboard.Children[0];

        // 同一帧内停掉再重设，回弹不可见
        storyboard.Stop();
        animation.From = brush.Color;
        animation.To = to;
        animation.Duration = new Duration(TimeSpan.FromMilliseconds(milliseconds));
        storyboard.Begin();
    }

    /// <summary>窗口关闭前的淡出时长。</summary>
    private const int CloseFadeMilliseconds = 100;

    /// <summary>关闭时窗口缩小到的比例。</summary>
    private const double CloseFadeScale = 0.9;

    /// <summary>
    /// 让整扇窗口的 XAML 内容一边缩小一边淡出，结束后回调。
    ///
    /// 动画的是外壳根容器的 RenderTransform 和 Opacity——不是改窗口透明度，
    /// 也不是 ShowWindow：内容先缩到 90% 并淡到全透明，100ms 后再真正 Close，
    /// 用户看到的是平滑消失而不是一瞬间没了。
    /// </summary>
    public void FadeOut(Action onDone)
    {
        var root = _outerFrame;
        if (root is null)
        {
            onDone();
            return;
        }

        // 缩放以窗口中心为锚点；从当前值起动画，避免已有变换时跳一下。
        root.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
        var scale = root.RenderTransform as ScaleTransform;
        if (scale is null)
        {
            scale = new ScaleTransform();
            root.RenderTransform = scale;
        }

        // 用 XAML Storyboard（Opacity + ScaleX + ScaleY），和 FadeBrush 同一套路子：
        // Composition 的 CreateScalarAnimation 在这版 WinAppSDK 元数据里不可用。
        var duration = new Duration(TimeSpan.FromMilliseconds(CloseFadeMilliseconds));
        var opacity = new DoubleAnimation
        {
            From = root.Opacity,
            To = 0,
            Duration = duration,
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(opacity, root);
        Storyboard.SetTargetProperty(opacity, nameof(root.Opacity));

        var scaleX = new DoubleAnimation
        {
            From = scale.ScaleX,
            To = CloseFadeScale,
            Duration = duration,
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(scaleX, scale);
        Storyboard.SetTargetProperty(scaleX, nameof(scale.ScaleX));

        var scaleY = new DoubleAnimation
        {
            From = scale.ScaleY,
            To = CloseFadeScale,
            Duration = duration,
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(scaleY, scale);
        Storyboard.SetTargetProperty(scaleY, nameof(scale.ScaleY));

        var storyboard = new Storyboard { Children = { opacity, scaleX, scaleY } };
        storyboard.Completed += (_, _) => onDone();
        storyboard.Begin();
    }

    private void Reset()
    {
        _nodes.Clear();
        _specs.Clear();
        _parentOf.Clear();

        // 刻意不清空 Content：旧内容（加载圈，或上一版界面）留到新树建好再整体替换。
        // 提前清空的话，建树那几百毫秒里窗口会是一片空白，比割裂感更难受。
    }

    /// <summary>
    /// render 失败时把加载圈换成错误提示。
    ///
    /// 只处理「表单本身有问题」这类失败（根节点缺失、根节点有父节点）。
    /// 协议层的致命错误会直接关掉宿主，那时窗口已经不在了，轮不到这里。
    /// </summary>
    private void ShowErrorView(string detail)
    {
        try
        {
            var icon = new FontIcon
            {
                Glyph = "\uE783",
                FontSize = 44,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 196, 43, 28)),
                HorizontalAlignment = HorizontalAlignment.Center,
            };

            var title = new TextBlock
            {
                Text = "加载失败",
                FontSize = 16,
                HorizontalAlignment = HorizontalAlignment.Center,
            };

            var stack = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Spacing = 12,
            };
            stack.Children.Add(icon);
            stack.Children.Add(title);

            if (!string.IsNullOrWhiteSpace(detail))
            {
                // 窗口此刻还是加载态那个小尺寸，所以细节文字要限宽换行
                stack.Children.Add(new TextBlock
                {
                    Text = detail,
                    FontSize = 12,
                    Opacity = 0.6,
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 220,
                    TextAlignment = TextAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                });
            }

            _window.Content = stack;

            // 标题也一起改，否则标题栏还挂着「正在加载」，和内容自相矛盾
            _window.Title = "加载失败";

            HostLog.Write("[render] error view shown: " + detail);
        }
        catch (Exception ex)
        {
            HostLog.Write("show error view failed: " + ex.Message);
        }
    }

    // ==================================================================
    // 增量补丁
    // ==================================================================

    public void Apply(PatchMessage message)
    {
        foreach (var op in message.Ops)
        {
            try
            {
                switch (op.Kind)
                {
                    case PatchOpKind.Set:
                        ApplySet(op);
                        break;
                    case PatchOpKind.Insert:
                        ApplyInsert(op);
                        break;
                    case PatchOpKind.Remove:
                        ApplyRemove(op);
                        break;
                    case PatchOpKind.Move:
                        ApplyMove(op);
                        break;
                    case PatchOpKind.Window:
                        ApplyWindowProps(op.Props);
                        break;
                }
            }
            catch (Exception ex)
            {
                _error("internal", $"patch 操作 {op.Kind} 失败: {ex.Message}", op.Id);
            }
        }
    }

    private void ApplySet(PatchOp op)
    {
        if (op.Id is null || !_nodes.TryGetValue(op.Id, out var element))
        {
            _error("unknown-node", $"set 的目标节点不存在: {op.Id}", op.Id);
            return;
        }

        ApplyPropsSuppressed(op.Id, element, op.Props);

        if (op.Events is not null && _specs.TryGetValue(op.Id, out var spec))
        {
            spec.Events = op.Events;
            BindEvents(op.Id, element, op.Events.Keys);
        }
    }

    private void ApplyInsert(PatchOp op)
    {
        if (op.Node is null || op.Parent is null)
        {
            _error("bad-op", "insert 缺少 node 或 parent", null);
            return;
        }

        var spec = op.Node;
        if (_nodes.ContainsKey(spec.Id))
        {
            _error("bad-protocol", $"insert 的节点 id 已存在: {spec.Id}", spec.Id);
            return;
        }

        if (!_nodes.TryGetValue(op.Parent, out var parentElement))
        {
            _error("unknown-node", $"insert 的父节点不存在: {op.Parent}", op.Parent);
            return;
        }

        if (!CanHaveChildren(parentElement))
        {
            _error("bad-protocol", $"节点 {op.Parent} 不是容器，无法插入子节点", op.Parent);
            return;
        }

        var element = NodeFactory.Create(spec.Type, spec.Id, spec.Props, _warn);
        _nodes[spec.Id] = element;
        _specs[spec.Id] = spec;
        if (element is WebView2 insertedWeb)
        {
            WebView2Support.Attach(insertedWeb, spec.Id, _emit, _warn);
        }

        ApplyPropsSuppressed(spec.Id, element, spec.Props);

        if (!AttachChild(parentElement, element, op.Index))
        {
            _nodes.Remove(spec.Id);
            _specs.Remove(spec.Id);
            _error("bad-protocol", $"节点 {spec.Id} 不能作为 {op.Parent} 的子节点", spec.Id);
            return;
        }
        _parentOf[spec.Id] = op.Parent;

        if (spec.Events is { Count: > 0 })
        {
            _bridge.Bind(element, spec.Id, spec.Events.Keys);
        }
    }

    private void ApplyRemove(PatchOp op)
    {
        if (op.Id is null || !_nodes.TryGetValue(op.Id, out var element))
        {
            _error("unknown-node", $"remove 的目标节点不存在: {op.Id}", op.Id);
            return;
        }

        var doomed = CollectSubtree(op.Id);

        TabView? ownerTabs = null;
        if (_parentOf.TryGetValue(op.Id, out var parentId)
            && _nodes.TryGetValue(parentId, out var owner))
        {
            ownerTabs = owner as TabView;
        }

        if (ownerTabs is not null && element is TabViewItem tab)
        {
            // 标签页 / 固定选项卡不在 Panel.Children 里，得从 TabItems 摘
            ownerTabs.TabItems.Remove(tab);
            ExtendedControls.UnregisterPinned(ownerTabs, tab);
        }
        else if (element.Parent is Panel parent)
        {
            parent.Children.Remove(element);
        }
        else if (ReferenceEquals(_window.Content, element))
        {
            _window.Content = null;
        }

        foreach (var id in doomed)
        {
            _nodes.Remove(id);
            _specs.Remove(id);
            _parentOf.Remove(id);
        }
    }

    private void ApplyMove(PatchOp op)
    {
        if (op.Id is null || op.Parent is null)
        {
            _error("bad-op", "move 缺少 id 或 parent", null);
            return;
        }

        if (!_nodes.TryGetValue(op.Id, out var element))
        {
            _error("unknown-node", $"move 的目标节点不存在: {op.Id}", op.Id);
            return;
        }

        if (!_nodes.TryGetValue(op.Parent, out var newParentElement))
        {
            _error("unknown-node", $"move 的新父节点不存在: {op.Parent}", op.Parent);
            return;
        }

        if (!CanHaveChildren(newParentElement))
        {
            _error("bad-protocol", $"节点 {op.Parent} 不是容器", op.Parent);
            return;
        }

        TabView? oldTabs = null;
        if (_parentOf.TryGetValue(op.Id, out var oldParentId)
            && _nodes.TryGetValue(oldParentId, out var oldOwner))
        {
            oldTabs = oldOwner as TabView;
        }

        if (oldTabs is not null && element is TabViewItem oldTab)
        {
            oldTabs.TabItems.Remove(oldTab);
            ExtendedControls.UnregisterPinned(oldTabs, oldTab);
        }
        else if (element.Parent is Panel oldPanel)
        {
            oldPanel.Children.Remove(element);
        }

        if (!AttachChild(newParentElement, element, op.Index))
        {
            _error("bad-protocol", $"节点 {op.Id} 不能移到 {op.Parent} 下", op.Id);
            return;
        }
        _parentOf[op.Id] = op.Parent;
    }

    /// <summary>
    /// 找到节点真正能挂子元素的地方。
    ///
    /// 普通容器本身就是 Panel；Scroll 节点是 ScrollViewer，它没有 Children，
    /// 真正装内容的是里面那层 StackPanel。把差异收在这里，
    /// 建树、插入、移动三条路径就不用各自判断一次。
    /// </summary>
    private static Panel? ChildHostOf(FrameworkElement element)
    {
        // WebView2 缺运行时的降级占位也是 Border，但它的 Child 是提示文字，
        // 不能让子节点把「加载失败」冲掉。
        if (element.Tag is "webview2-fallback")
        {
            return null;
        }

        return element switch
        {
            Panel panel => panel,
            ScrollViewer { Content: Panel inner } => inner,
            // 扩展容器在创建时都预置了一个内层 StackPanel，
            // 于是这里依旧只返回 Panel，建树/插入/移动三条路径完全不用改。
            Border { Child: Panel borderInner } => borderInner,
            Viewbox { Child: Panel viewInner } => viewInner,
            Expander { Content: Panel expanderInner } => expanderInner,
            NavigationView { Content: Panel navInner } => navInner,
            RefreshContainer { Content: Panel refreshInner } => refreshInner,
            TwoPaneView { Pane1: Panel pane1 } => pane1,
            // TabPage：TabViewItem.Content 里预置的 StackPanel，
            // 于是标签页也能像 Column 一样直接放子控件。
            TabViewItem { Content: Panel tabInner } => tabInner,
            _ => null,
        };
    }

    /// <summary>能不能挂子节点。TabView 的子节点是标签，不是 Panel.Children。</summary>
    private static bool CanHaveChildren(FrameworkElement element) =>
        element is TabView || ChildHostOf(element) is not null;

    /// <summary>
    /// 统一挂载入口：建树、insert、move 三条路径共用。
    ///
    /// TabView 的两类子元素都是 TabViewItem：
    /// - PinnedTab 插到「固定标签块」的末尾，永远排在普通标签前面；
    /// - TabPage 追加到 TabItems 末尾，受 WinUI 内置拖动重排 / 关闭机制管理。
    ///
    /// 两类标签都忽略协议里的 index：标签条的真实顺序由用户拖动决定，
    /// 用 Python 侧 children 的下标去硬插反而会错位。
    /// </summary>
    private static bool AttachChild(FrameworkElement parent, FrameworkElement child, int index)
    {
        if (parent is TabView tabs)
        {
            if (child is TabViewItem tab)
            {
                if (ExtendedControls.IsPinnedTab(tab))
                {
                    // 固定标签块内部保持添加顺序，整块永远在普通标签之前
                    var insertAt = 0;
                    while (insertAt < tabs.TabItems.Count
                           && ExtendedControls.IsPinnedTab(tabs.TabItems[insertAt]))
                    {
                        insertAt++;
                    }

                    tabs.TabItems.Insert(insertAt, tab);
                    ExtendedControls.RegisterPinned(tabs, tab);
                }
                else
                {
                    tabs.TabItems.Add(tab);
                }

                return true;
            }

            return false;
        }

        if (ChildHostOf(parent) is not { } panel)
        {
            return false;
        }

        panel.Children.Insert(Math.Clamp(index, 0, panel.Children.Count), child);
        return true;
    }

    /// <summary>
    /// 收集某个节点及其全部子孙的 id。删除时要一起清出节点表。
    ///
    /// 旧实现每出队一个节点就遍历整个 _parentOf，还用 List.Contains 判重，
    /// 删一棵 N 个节点的子树是 O(N²·M)；examples/03_live.py 里逐行 destroy()
    /// 会把这个成本放大成明显的卡顿。先一次性建邻接表再 BFS，整体 O(N+M)。
    /// </summary>
    private List<string> CollectSubtree(string rootId)
    {
        var childrenOf = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var pair in _parentOf)
        {
            if (!childrenOf.TryGetValue(pair.Value, out var list))
            {
                list = new List<string>();
                childrenOf[pair.Value] = list;
            }
            list.Add(pair.Key);
        }

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal) { rootId };
        var queue = new Queue<string>();
        queue.Enqueue(rootId);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            result.Add(current);
            if (!childrenOf.TryGetValue(current, out var children))
            {
                continue;
            }

            foreach (var child in children)
            {
                if (seen.Add(child))
                {
                    queue.Enqueue(child);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// 供 MicroRuntime 的 set 指令使用：走和普通 patch 完全相同的属性应用链
    /// （含回声抑制），但不需要组装一条 patch 消息。
    /// </summary>
    public void ApplyMicroProps(string nodeId, JsonObject props)
    {
        if (!_nodes.TryGetValue(nodeId, out var element))
        {
            throw new InvalidOperationException($"micro set 的节点不存在: {nodeId}");
        }

        ApplyPropsSuppressed(nodeId, element, props);
    }

    /// <summary>应用属性，期间把元素标记为「程序在改」，压住回声事件。</summary>
    private void ApplyPropsSuppressed(string nodeId, FrameworkElement element, JsonObject? props)
    {
        _bridge.BeginSuppress(element);
        try
        {
            _applier.Apply(nodeId, element, props);
        }
        finally
        {
            _bridge.EndSuppress(element);
        }
    }

    /// <summary>
    /// 分发事件名：根节点上的窗口级事件挂到 Window 上，其余挂到元素本身。
    /// Python 侧统一用 <c>win.bind("closing", ...)</c>，不需要知道宿主把处理器挂在哪。
    /// </summary>
    private void BindEvents(string nodeId, FrameworkElement element, IEnumerable<string> names)
    {
        List<string>? windowEvents = null;
        List<string>? elementEvents = null;

        foreach (var name in names)
        {
            if (nodeId == _rootId && name is
                "closing" or "sizeChanged" or "windowActivated" or "windowStateChanged")
            {
                (windowEvents ??= new List<string>()).Add(name);
            }
            else
            {
                (elementEvents ??= new List<string>()).Add(name);
            }
        }

        if (windowEvents is not null)
        {
            foreach (var name in windowEvents)
            {
                _bridge.BindWindow(_window, nodeId, name);
            }
        }

        if (elementEvents is not null)
        {
            _bridge.Bind(element, nodeId, elementEvents);
        }
    }

    // ==================================================================
    // 窗口
    // ==================================================================

    /// <summary>
    /// true 表示这扇窗已经被嵌成父窗口的 WS_CHILD 子窗口，
    /// 几何由 EmbeddedWindow 用 SetWindowPos 管，渲染层不能再 Resize 或动 presenter。
    /// </summary>
    /// <summary>
    /// 嵌入的子窗口（WS_CHILD）。
    ///
    /// 子窗口不需要外围那圈透明边：它没有 DWM 那一套，边框留着只是白占地方。
    /// </summary>
    public bool Embedded
    {
        get => _embedded;
        set
        {
            _embedded = value;
            UpdateChrome();
            UpdateInnerBackground();
        }
    }

    /// <summary>
    /// 窗口外壳自绘的底色。
    ///
    /// 子窗口也**要**有不透明的底色：它下面就是父窗口的内容，完全透明的话
    /// 父窗口的文字会直接透上来（子窗口是 WS_CHILD，永远画在父窗口之上，
    /// 透明处照样能看见下层）。
    ///
    /// 之前那块「矩形白边」不是这里来的，是 **Window 根 Visual** 自带的默认
    /// 底色——根 Visual 是矩形，不管里面的内容怎么裁都会露出来。
    /// 那个由 TransparentBackdrop 负责置透明，这里这层照旧自绘圆角白底，
    /// 两者不冲突：根 Visual 透明（矩形白边没了）+ 这层圆角白底（挡住下层文字）。
    /// </summary>
    private void UpdateInnerBackground()
    {
        if (_innerFrame is null)
        {
            return;
        }

        _innerFrame.Background = new SolidColorBrush(ContentBackground());
    }

    /// <summary>嵌入子窗口的自绘标题栏点了关闭，由 Session 决定是发事件还是直接关。</summary>
    public Action? CloseButtonPressed { get; set; }

    /// <summary>嵌入子窗口的自绘标题栏点了最小化（折叠到父窗口左下角）。</summary>
    public Action? MinimizeButtonPressed { get; set; }

    /// <summary>标题栏的最大化 / 还原按钮，双击标题栏也走这里。</summary>
    public Action? MaximizeButtonPressed { get; set; }

    /// <summary>标题栏应用图标左边的返回按钮，点了发 returnRequested 给 Python。</summary>
    public Action? ReturnButtonPressed { get; set; }

    /// <summary>
    /// 嵌入子窗口当前是否折叠。标题栏的最小化按钮图标据此切换：
    /// 折叠后变成「还原」，展开时是「最小化」。
    /// </summary>
    public bool EmbeddedCollapsed { get; set; }

    /// <summary>标题栏上那个会变的图标（最小化 / 还原二合一）。</summary>
    private FontIcon? _minimizeGlyph;

    /// <summary>最大化 / 还原二合一的图标。</summary>
    private FontIcon? _maximizeGlyph;

    /// <summary>标题栏上的标题文字，跟随 Window.Title 更新。</summary>
    private TextBlock? _titleText;

    /// <summary>窗口外壳：第一行标题栏，第二行内容。只建一次，之后只换内容区。</summary>
    private Grid? _shell;

    /// <summary>内容区的宿主。控件树替换的是它，标题栏不受影响。</summary>
    private Grid? _contentHost;

    /// <summary>圆角几何，尺寸变化时同步（Composition 裁剪带抗锯齿）。</summary>
    private CompositionRoundedRectangleGeometry? _clipGeometry;

    /// <summary>外围透明边那一层。</summary>
    private Border? _outerFrame;

    /// <summary>真正画内容的容器（白底 + 圆角 + 外框线）。</summary>
    private Border? _innerFrame;

    /// <summary>自绘标题栏。最大化时要取消它的上两角圆角。</summary>
    private Border? _titleBarBorder;

    /// <summary>标题栏上的窗口图标。</summary>
    private Microsoft.UI.Xaml.Controls.Image? _titleIcon;

    /// <summary>标题栏上的最大化按钮（可能要禁用）。</summary>
    private Border? _maximizeBox;

    /// <summary>标题栏上的最小化按钮。</summary>
    private Border? _minimizeBox;

    /// <summary>标题栏上的关闭按钮。</summary>
    private Border? _closeBox;

    /// <summary>应用图标左边的返回按钮。</summary>
    private Border? _returnBox;

    /// <summary>标题栏里装左侧部件与右侧按钮的网格：左边距随返回按钮显隐变化。</summary>
    private Grid? _titleBarInner;

    /// <summary>标题右边的带图标输入框（整个格子，控制显隐）。</summary>
    private Grid? _titleInputHost;

    /// <summary>标题栏输入框本体，供运行期读写文本。</summary>
    private TextBox? _titleInput;

    /// <summary>标题栏是否显示返回按钮。</summary>
    private bool _returnButton;

    /// <summary>标题栏是否显示输入框。</summary>
    private bool _textInputBox;

    /// <summary>
    /// 最大化是否交给 Python 回调处理。
    ///
    /// 没挂回调时按钮是禁用的——宿主不自己决定要不要最大化。
    /// </summary>
    private bool _maximizeRequestable;

    private bool _embedded;

    /// <summary>标题栏是否正在被拖动（指针事件驱动，不用系统移动循环）。</summary>
    private bool _draggingTitleBar;

    /// <summary>切换最小化按钮的图标外观。</summary>
    public void SetCollapsedVisual(bool collapsed)
    {
        EmbeddedCollapsed = collapsed;
        if (_minimizeGlyph is not null)
        {
            _minimizeGlyph.Glyph = collapsed ? "\uE923" : "\uE921";
        }
    }

    private void ApplyWindowProps(JsonObject? props)
    {
        if (props is null)
        {
            return;
        }

        var spec = new WindowSpec
        {
            Title = ValueParsers.AsString(props["title"]),
            Width = ValueParsers.AsDouble(props["width"]),
            Height = ValueParsers.AsDouble(props["height"]),
            MinWidth = ValueParsers.AsDouble(props["minWidth"]),
            MinHeight = ValueParsers.AsDouble(props["minHeight"]),
            Resizable = ValueParsers.AsBool(props["resizable"]),
            Center = ValueParsers.AsBool(props["center"]),
            X = ValueParsers.AsDouble(props["x"]),
            Y = ValueParsers.AsDouble(props["y"]),
            Icon = ValueParsers.AsString(props["icon"]),
            AlwaysOnTop = ValueParsers.AsBool(props["alwaysOnTop"]),
            MaximizeRequestable = ValueParsers.AsBool(props["maximizeRequestable"]),
            ReturnButton = ValueParsers.AsBool(props["returnButton"])
                ?? ValueParsers.AsBool(props["RetrunButtom"]),
            TextInputBox = ValueParsers.AsBool(props["textInputBox"])
                ?? ValueParsers.AsBool(props["TextInputBox"]),
            Minimizable = ValueParsers.AsBool(props["minimizable"]),
            Maximizable = ValueParsers.AsBool(props["maximizable"]),
            Maximized = ValueParsers.AsBool(props["maximized"]),
            Theme = ValueParsers.AsString(props["theme"]),
            Backdrop = ValueParsers.AsString(props["backdrop"]),
            Scroll = ValueParsers.AsBool(props["scroll"]),
        };

        // 补丁路径下内容已经在窗口上，可以就地换包裹层
        ApplyWindowSpec(spec, contentReady: true);
    }

    /// <param name="contentReady">
    /// 为 true 表示窗口里已经有内容，可以就地调整「要不要滚动」。
    /// 首次 render 传 false——那时新树还没挂上去，包旧内容纯属白做。
    /// </param>
    private void ApplyWindowSpec(WindowSpec spec, bool contentReady = false)
    {
        try
        {
            if (!string.IsNullOrEmpty(spec.Title))
            {
                _window.Title = spec.Title;
            }

            // 最大化是否交给 Python 回调——没挂回调就把按钮禁用。
            if (spec.MaximizeRequestable is { } requestable)
            {
                SetMaximizeRequestable(requestable);
            }

            // 标题栏上的可选部件：返回按钮 / 带图标输入框。
            // 首次 render 时标题栏还没建，这里先把状态存下来，EnsureShell 会补上。
            if (spec.ReturnButton is { } returnButton)
            {
                _returnButton = returnButton;
            }

            if (spec.TextInputBox is { } textInputBox)
            {
                _textInputBox = textInputBox;
            }

            ApplyTitleBarOptions();

            if (Embedded)
            {
                // 子窗口的尺寸/位置/presenter 全归 EmbeddedWindow 管。
                // 这里只要碰 AppWindow.Resize，刚 SetWindowPos 摆好的几何就被打回默认值。
                if (spec.Theme is { Length: > 0 } embeddedTheme)
                {
                    _pendingTheme = embeddedTheme;
                    ApplyTheme(embeddedTheme);
                }

                return;
            }

            HostLog.Write("[window] title ok");

            var appWindow = _window.AppWindow;
            var scale = GetDpiScale();
            HostLog.Write($"[window] appWindow ok scale={scale}");

            if (spec.Width is { } width || spec.Height is { } height)
            {
                var current = appWindow.Size;
                var pixelWidth = spec.Width is { } w
                    ? (int)Math.Round(w * scale)
                    : current.Width;
                var pixelHeight = spec.Height is { } h
                    ? (int)Math.Round(h * scale)
                    : current.Height;

                appWindow.Resize(new SizeInt32(pixelWidth, pixelHeight));
                HostLog.Write($"[window] resized to {pixelWidth}x{pixelHeight}");
            }

            if (appWindow.Presenter is OverlappedPresenter presenter)
            {
                // 走到这里说明内容已经渲染成功，先把加载态收走的窗口能力还给用户，
                // 再用协议里的显式值覆盖。顺序反了的话上一步会把这里的设置抹掉。
                presenter.IsMinimizable = true;
                presenter.IsMaximizable = true;
                presenter.IsResizable = true;

                if (spec.Resizable is { } resizable)
                {
                    presenter.IsResizable = resizable;
                }

                if (spec.Minimizable is { } minimizable)
                {
                    presenter.IsMinimizable = minimizable;
                }

                if (spec.Maximizable is { } maximizable)
                {
                    presenter.IsMaximizable = maximizable;
                }

                if (spec.AlwaysOnTop is { } alwaysOnTop)
                {
                    presenter.IsAlwaysOnTop = alwaysOnTop;
                }

                if (spec.MinWidth is { } minWidth)
                {
                    presenter.PreferredMinimumWidth = (int)Math.Round(minWidth * scale);
                }

                if (spec.MinHeight is { } minHeight)
                {
                    presenter.PreferredMinimumHeight = (int)Math.Round(minHeight * scale);
                }

                HostLog.Write("[window] presenter ok");
            }

            if (spec.Center == true)
            {
                CenterOnScreen(appWindow);
                HostLog.Write("[window] centered");
            }

            // 显式坐标优先于居中：两者同时给时按坐标落地
            if (spec.X is { } posX || spec.Y is { } posY)
            {
                var current = appWindow.Position;
                appWindow.Move(new PointInt32(
                    spec.X is { } x ? (int)Math.Round(x * scale) : current.X,
                    spec.Y is { } y ? (int)Math.Round(y * scale) : current.Y));
                HostLog.Write("[window] moved");
            }

            ApplyWindowIcon(appWindow, spec.Icon);

            if (spec.Maximized is { } maximized
                && appWindow.Presenter is OverlappedPresenter windowPresenter)
            {
                if (maximized)
                {
                    windowPresenter.Maximize();
                }
                else
                {
                    windowPresenter.Restore();
                }

                HostLog.Write($"[window] maximized={maximized}");
            }

            if (spec.Theme is { Length: > 0 } theme)
            {
                _pendingTheme = theme;
                ApplyTheme(theme);
                HostLog.Write("[window] theme ok");
            }

            if (spec.Backdrop is { Length: > 0 } backdrop)
            {
                HostLog.Write($"[window] applying backdrop \"{backdrop}\"");
                ApplyBackdrop(backdrop);
                HostLog.Write("[window] backdrop ok");
            }

            if (contentReady && spec.Scroll is { } scroll)
            {
                ApplyScrollMode(scroll);
            }
        }
        catch (Exception ex)
        {
            _warn($"窗口属性应用失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 把根节点包进 ScrollViewer。
    ///
    /// 几个参数刻意不取默认值：
    /// - 关掉横向滚动：界面是纵向流式布局，横向滚动只会带来误触
    /// - HorizontalContentAlignment 改成 Stretch：ScrollViewer 默认是 Left，
    ///   会让原本撑满宽度的子元素突然缩到内容宽度，看起来像布局坏了
    /// </summary>
    private static ScrollViewer WrapForScroll(FrameworkElement root) => new()
    {
        Content = root,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        HorizontalScrollMode = ScrollMode.Disabled,
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
        VerticalContentAlignment = VerticalAlignment.Top,
    };

    /// <summary>运行中切换整窗滚动。只在窗口补丁路径调用。</summary>
    private void ApplyScrollMode(bool enabled)
    {
        if (_rootId is not { } id || !_nodes.TryGetValue(id, out var root))
        {
            return;
        }

        if (_contentHost is null)
        {
            return;
        }

        var current = _contentHost.Children.Count > 0 ? _contentHost.Children[0] : null;

        if (enabled && current is not ScrollViewer)
        {
            // 必须先摘下来：同一个控件挂在两个父容器上，WinUI 会直接杀进程
            _contentHost.Children.Clear();
            _contentHost.Children.Add(WrapForScroll(root));
            HostLog.Write("[window] scroll on");
        }
        else if (!enabled && current is ScrollViewer scroller)
        {
            scroller.Content = null;
            _contentHost.Children.Clear();
            _contentHost.Children.Add(root);
            HostLog.Write("[window] scroll off");
        }
    }

    /// <summary>
    /// 设置窗口图标。只接受磁盘上的绝对/相对路径，且文件必须存在——
    /// AppWindow.SetIcon 对不存在的路径是静默忽略，不返回错误，很难排查。
    /// </summary>
    private void ApplyWindowIcon(AppWindow appWindow, string? iconPath)
    {
        if (string.IsNullOrWhiteSpace(iconPath))
        {
            return;
        }

        try
        {
            var full = Path.IsPathRooted(iconPath) ? iconPath : Path.GetFullPath(iconPath);
            if (!File.Exists(full))
            {
                _warn($"窗口图标不存在: {full}");
                return;
            }

            appWindow.SetIcon(full);
            HostLog.Write($"[window] icon \"{full}\"");

            // 系统图标换了，标题栏那颗也跟着换（从窗口属性重新读一次）
            _ = RefreshTitleIconAsync();
        }
        catch (Exception ex)
        {
            _warn($"窗口图标设置失败: {ex.Message}");
        }
    }

    private void ApplyTheme(string? theme)
    {
        if (string.IsNullOrEmpty(theme) || _window.Content is not FrameworkElement root)
        {
            return;
        }

        root.RequestedTheme = theme.ToLowerInvariant() switch
        {
            "dark" => ElementTheme.Dark,
            "light" => ElementTheme.Light,
            _ => ElementTheme.Default,
        };
    }

    private void ApplyBackdrop(string requested)
    {
        // 先把请求解析成这台机器真能做的效果，Win10 上 Mica 会被降级成 Acrylic
        var kind = Capabilities.ResolveBackdrop(requested);

        try
        {
            _window.SystemBackdrop = kind switch
            {
                "mica" => new MicaBackdrop(),
                "acrylic" => new DesktopAcrylicBackdrop(),
                _ => null,
            };
        }
        catch (Exception ex)
        {
            _warn($"backdrop \"{kind}\" 应用失败: {ex.Message}");
        }
    }

    private void CenterOnScreen(AppWindow appWindow)
    {
        var display = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary);
        var work = display.WorkArea;
        var size = appWindow.Size;

        appWindow.Move(new PointInt32(
            work.X + (work.Width - size.Width) / 2,
            work.Y + (work.Height - size.Height) / 2));
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    /// <summary>
    /// 协议里的尺寸是逻辑像素（DIP），而 AppWindow 用的是物理像素，
    /// 中间差一个显示器缩放系数。125% 缩放下不换算窗口会小一圈。
    /// </summary>
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
}
