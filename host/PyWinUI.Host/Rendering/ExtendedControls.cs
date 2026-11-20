using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using XamlPath = Microsoft.UI.Xaml.Shapes.Path;

namespace PyWinUI.Host.Rendering;

/// <summary>
/// 扩展控件工厂：补齐 WinUI 3 常用但首版没接进来的控件类型。
///
/// 返回 null 表示这个类型不归本文件管，由 NodeFactory 继续走原逻辑。
/// 之所以单独拆文件，是因为这些控件的「创建 + 属性 + 事件」是一整组新增能力，
/// 混进原 switch 会让原本稳定的基础控件代码变得难读。
///
/// 容器型控件（Viewbox / BorderBox / Expander / TwoPaneView / RefreshContainer）
/// 一律预置一个内层 StackPanel：FormRenderer.ChildHostOf 只认 Panel，
/// 有这层壳子节点才能像 Column / Row 一样直接挂进去。
/// </summary>
internal static class ExtendedControls
{
    public static FrameworkElement? Create(string type, JsonObject? props)
    {
        switch (type)
        {
            // ---------------- 表单输入 ----------------
            case "NumberBox":
                // 不设 HorizontalAlignment.Stretch：在 StackPanel 的无限宽测量里
                // NumberBox 会被量成 0 宽，反而什么都不显示。
                return new NumberBox
                {
                    SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
                    MinWidth = 160,
                };

            case "PasswordBox":
                return new PasswordBox();

            case "RichTextBlock":
                return new RichTextBlock { TextWrapping = TextWrapping.Wrap };

            // ---------------- 画布与图元 ----------------
            // Canvas 是 Panel 子类，ChildHostOf 直接认，子节点挂载规则不用改。
            case "Canvas":
                return new Canvas
                {
                    // 不裁切：图元允许超出画布边界（和 SVG 的 overflow:visible 一致）
                    Clip = null,
                };

            // Win2D 立即模式画布：整块图元数组一次下发，GPU 重绘
            case "Canvas2D":
                return Canvas2DSupport.Create();

            // D3D11 三维视口：真 GPU 管线，有深度缓冲与着色器
            case "Viewport3D":
                return Viewport3DSupport.Create();

            // 图元默认描边 1px、无填充，具体值由 fill / stroke / strokeWidth 属性覆盖
            case "Rect":
                return new Rectangle { StrokeThickness = 1 };

            case "Ellipse":
                return new Ellipse { StrokeThickness = 1 };

            case "Line":
                return new Line { StrokeThickness = 1 };

            case "Path":
                return new XamlPath { StrokeThickness = 1 };

            case "Polygon":
                return new Polygon { StrokeThickness = 1 };

            case "Polyline":
                return new Polyline { StrokeThickness = 1 };

            case "RichEditBox":
                return new RichEditBox();

            case "Icon":
                // 单独暴露成节点类型：items.children 里放图标是最常见的需求，
                // 之前只能塞 Label 显示 emoji。字形用 Segoe Fluent Icons 码点。
                return new FontIcon { FontSize = 16 };

            case "AutoSuggestBox":
                return new AutoSuggestBox();

            case "DatePicker":
                return new DatePicker();

            case "TimePicker":
                return new TimePicker();

            case "CalendarDatePicker":
                return new CalendarDatePicker();

            case "CalendarView":
                return new CalendarView { SelectionMode = CalendarViewSelectionMode.Single };

            case "RatingControl":
                return new RatingControl();

            case "ColorPicker":
                // 自绘调色框：不用 WinUI 原生 ColorPicker（窄宽度会挤成一条线，
                // 外观也随主题/DPI 漂移）。对外 color 属性 / colorChanged 事件不变。
                return new CustomColorPicker();

            case "DropDownButton":
                return new DropDownButton { Content = "选择" };

            case "ToggleSplitButton":
                return new ToggleSplitButton { Content = "选项" };

            case "PersonPicture":
                return new PersonPicture { Width = 64, Height = 64 };

            // ---------------- 数据 / 导航 ----------------
            case "GridView":
                return new GridView { IsItemClickEnabled = true, SelectionMode = ListViewSelectionMode.Single };

            case "FlipView":
                return new FlipView();

            case "Pivot":
                return new Pivot();

            case "TabView":
            {
                // 两类子元素都是真正的 TabViewItem，区别只在关闭与拖动：
                // - PinnedTab：IsClosable=false，拖动在 TabDragStarting 里取消，
                //   并且每次拖动结束都把固定的那几个挪回最前面；
                // - TabPage：可关闭、可拖动重排。
                var tabs = new TabView
                {
                    IsAddTabButtonVisible = false,
                    CanReorderTabs = true,
                };

                tabs.TabDragStarting += (_, e) =>
                {
                    if (IsPinnedTab(e.Tab))
                    {
                        e.Cancel = true;
                    }
                };

                // 用户把普通标签拖到固定标签前面时，WinUI 会真的交换位置。
                // 拖动结束后把固定标签整块挪回最前面，保证「不可移动」的最终状态。
                tabs.TabDragCompleted += (_, _) => NormalizePinnedTabs(tabs);

                // 关键：标签条内部的重排走的是 TabView 模板里的 ListView，
                // 上面两个 TabDrag* 事件只在「把标签拖出标签条」时触发。
                // 不挂内部 ListView 的话，普通标签可以直接把固定标签挤走。
                tabs.Loaded += (_, _) => HookReorder(tabs);
                return tabs;
            }

            case "PinnedTab":
            {
                // 固定选项卡就是「不能关、不能拖」的标签页：同样是 TabViewItem，
                // 同样有图标、同样能放子控件，只是没有关闭按钮。
                var pinned = new TabViewItem
                {
                    IsClosable = false,
                    CanDrag = false,
                    Tag = PinnedTabTag,
                    Content = new StackPanel { Orientation = Orientation.Vertical },
                };

                // 条内重排既不触发 TabDragStarting 也不触发 DragItemsStarting，
                // 没有能 Cancel 的事件。退一步：在按下的这一小段时间里把
                // CanReorderTabs 关掉，让内部 ListView 不要进入拖动状态。
                pinned.PointerPressed += (_, _) =>
                {
                    if (FindAncestor<TabView>(pinned) is { } owner)
                    {
                        owner.CanReorderTabs = false;
                    }
                    ScheduleReorderRestore(pinned);
                };

                pinned.PointerReleased += (_, _) => RestoreReorder(pinned);
                pinned.PointerCaptureLost += (_, _) => RestoreReorder(pinned);
                pinned.PointerCanceled += (_, _) => RestoreReorder(pinned);
                return pinned;
            }

            case "TabPage":
                // 内层 StackPanel 让 TabPage 能像 Column 一样直接放子控件，
                // 也顺手补上 README 第 8 章「动态条目不能塞子控件」里 TabView 那条。
                return new TabViewItem
                {
                    IsClosable = true,
                    Tag = "tab-page",
                    Content = new StackPanel { Orientation = Orientation.Vertical },
                };

            case "TreeView":
                return new TreeView();

            case "NavigationView":
                return new NavigationView
                {
                    IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed,
                    IsSettingsVisible = false,
                    PaneDisplayMode = NavigationViewPaneDisplayMode.Left,
                    Content = new StackPanel { Orientation = Orientation.Vertical },
                };

            case "MenuBar":
                return new MenuBar();

            case "CommandBar":
                return new CommandBar();

            case "BreadcrumbBar":
                return new BreadcrumbBar();

            case "ItemsView":
                return new ItemsView();

            case "RadioButtons":
                return new RadioButtons();

            case "SelectorBar":
                return new SelectorBar();

            // ---------------- 信息 / 容器 ----------------
            case "InfoBar":
                return new InfoBar { IsOpen = true, IsClosable = false };

            case "Expander":
                return new Expander
                {
                    Content = new StackPanel { Orientation = Orientation.Vertical },
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                };

            case "BorderBox":
                return new Border
                {
                    Child = new StackPanel { Orientation = Orientation.Vertical },
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                };

            case "Viewbox":
                return new Viewbox
                {
                    Child = new StackPanel { Orientation = Orientation.Vertical },
                    Stretch = Stretch.Uniform,
                };

            case "RelativePanel":
                return new RelativePanel();

            case "TwoPaneView":
                return new TwoPaneView { Pane1 = new StackPanel { Orientation = Orientation.Vertical } };

            case "RefreshContainer":
                return new RefreshContainer { Content = new StackPanel { Orientation = Orientation.Vertical } };

            default:
                return null;
        }
    }

    /// <summary>这些类型在协议里允许带 children。</summary>
    public static bool IsContainerType(string type) => type is
        "BorderBox" or "Viewbox" or "RelativePanel" or "Expander" or
        "TwoPaneView" or "RefreshContainer" or "NavigationView";

    // ------------------------------------------------------------------
    // 固定选项卡：真正的标签页，只是不能关、不能拖
    // ------------------------------------------------------------------

    /// <summary>固定选项卡的标记，挂在 TabViewItem.Tag 上。</summary>
    public const string PinnedTabTag = "pinned-tab";

    private const string ReorderHookedTag = "tab-reorder-hooked";

    private sealed class PinnedState
    {
        public readonly List<TabViewItem> Order = new();
    }

    /// <summary>
    /// 每个 TabView 里固定标签的「规范顺序」。
    ///
    /// WinUI 的条内重排既不触发 TabView.TabDragStarting，也不触发内部
    /// ListView.DragItemsStarting，没有可取消的事件可挂；唯一可靠的办法是
    /// 记住它们本该待的位置，任何顺序变化后按这份记录复位。
    /// </summary>
    private static readonly ConditionalWeakTable<TabView, PinnedState> PinnedStates = new();

    /// <summary>新固定标签挂上去时登记它的规范位置（追加在固定块末尾）。</summary>
    public static void RegisterPinned(TabView tabs, TabViewItem tab)
    {
        var state = PinnedStates.GetOrCreateValue(tabs);
        if (!state.Order.Contains(tab))
        {
            state.Order.Add(tab);
        }
    }

    public static void UnregisterPinned(TabView tabs, TabViewItem tab)
    {
        if (PinnedStates.TryGetValue(tabs, out var state))
        {
            state.Order.Remove(tab);
        }
    }

    /// <summary>
    /// 挂到 TabView 模板内部的 ListView 上，接管标签条内的拖动重排。
    ///
    /// WinUI 的 TabView 内部用 ListView 承载标签，条内重排是 ListView 的
    /// drag-reorder，**不会**触发 TabView.TabDragStarting / TabDragCompleted
    /// （那两个是撕裂拖动，把标签拖到别的 TabView 时才有）。
    /// 所以在 ListView 上：拖动固定标签时取消，拖动结束后把固定块归位。
    /// </summary>
    private static void HookReorder(TabView tabs)
    {
        if (tabs.Tag as string == ReorderHookedTag)
        {
            return;
        }

        if (FindDescendant<ListView>(tabs) is not { } list)
        {
            return;
        }

        tabs.Tag = ReorderHookedTag;
        HostLog.Write("[tabs] reorder hooked on internal ListView");

        // TabView 会在容器准备阶段把 TabViewItem.CanDrag 置回 true，
        // 所以在工厂里设的 false 会被盖掉。这里在容器生成完成后再压一次。
        list.ContainerContentChanging += (_, e) =>
        {
            if (e.InRecycleQueue)
            {
                return;
            }

            if (e.Item is TabViewItem tab && IsPinnedTab(tab)
                && e.ItemContainer is UIElement container)
            {
                container.CanDrag = false;
            }
        };

        void LockPinnedContainers()
        {
            foreach (var item in tabs.TabItems)
            {
                if (IsPinnedTab(item) && list.ContainerFromItem(item) is UIElement container)
                {
                    container.CanDrag = false;
                }
            }
        }

        LockPinnedContainers();
        _ = tabs.DispatcherQueue.TryEnqueue(LockPinnedContainers);

        list.DragItemsStarting += (_, e) =>
        {
            foreach (var item in e.Items)
            {
                if (IsPinnedTab(item))
                {
                    e.Cancel = true;
                    return;
                }
            }
        };

        list.DragItemsCompleted += (_, _) =>
        {
            // 事件回调里不要直接改集合，排到下一个 UI 回合再归位
            _ = tabs.DispatcherQueue.TryEnqueue(() => NormalizePinnedTabs(tabs));
        };

        // 兜底：不管顺序是被拖动、键盘还是别的途径改的，只要 TabItems 变了
        // 就归位一次。Normalize 是幂等的（位置对了就不动），不会自激循环。
        tabs.TabItemsChanged += (_, _) =>
        {
            _ = tabs.DispatcherQueue.TryEnqueue(() => NormalizePinnedTabs(tabs));
        };
    }

    private static T? FindAncestor<T>(DependencyObject node) where T : DependencyObject
    {
        for (var current = VisualTreeHelper.GetParent(node);
             current is not null;
             current = VisualTreeHelper.GetParent(current))
        {
            if (current is T match)
            {
                return match;
            }
        }

        return null;
    }

    private static void ScheduleReorderRestore(TabViewItem pinned)
    {
        // 松手事件偶尔会丢（拖到别的窗口、指针取消），加个短定时兜底，
        // 否则一次误操作会把整个 TabView 的重排永久关掉。
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.2) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            RestoreReorder(pinned);
        };
        timer.Start();
    }

    private static void RestoreReorder(TabViewItem pinned)
    {
        if (FindAncestor<TabView>(pinned) is { } owner && !owner.CanReorderTabs)
        {
            owner.DispatcherQueue.TryEnqueue(() => owner.CanReorderTabs = true);
        }
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        var queue = new Queue<DependencyObject>();
        queue.Enqueue(root);

        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            var count = VisualTreeHelper.GetChildrenCount(node);
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(node, i);
                if (child is T match)
                {
                    return match;
                }

                queue.Enqueue(child);
            }
        }

        return null;
    }

    /// <summary>
    /// 设置标签标题。用 TextBlock 而不是字符串，是为了能开
    /// <c>TextTrimming.CharacterEllipsis</c>：标签被 maxWidth 卡住时，
    /// 超出部分显示 "…" 而不是被硬裁掉。
    /// </summary>
    public static void SetTabHeader(TabViewItem tab, string text)
    {
        if (tab.Header is TextBlock existing)
        {
            existing.Text = text;
            return;
        }

        tab.Header = new TextBlock
        {
            Text = text,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    /// <summary>读回标签标题原文（Header 可能是 TextBlock）。</summary>
    public static string TabHeaderText(TabViewItem tab) =>
        tab.Header is TextBlock block ? block.Text : tab.Header?.ToString() ?? "";

    public static bool IsPinnedTab(object? item) =>
        item is TabViewItem tab && (tab.Tag as string) == PinnedTabTag;

    /// <summary>
    /// 把所有固定选项卡按原相对顺序挪回标签条最前面。
    ///
    /// 只拦 TabDragStarting 不够：用户可以把普通标签拖到第一个固定标签之前，
    /// 那一下 WinUI 会真的插入到前面。拖动结束后统一归位，最终状态才是对的。
    /// </summary>
    private static void NormalizePinnedTabs(TabView tabs)
    {
        var pinned = new List<TabViewItem>();
        foreach (var item in tabs.TabItems)
        {
            if (IsPinnedTab(item))
            {
                pinned.Add((TabViewItem)item);
            }
        }

        if (pinned.Count == 0)
        {
            return;
        }

        List<TabViewItem> desired;
        if (PinnedStates.TryGetValue(tabs, out var state))
        {
            desired = state.Order.Where(pinned.Contains).ToList();
            desired.AddRange(pinned.Where(item => !desired.Contains(item)));
        }
        else
        {
            desired = pinned;
        }

        for (var target = 0; target < desired.Count; target++)
        {
            var current = tabs.TabItems.IndexOf(desired[target]);
            if (current < 0 || current == target)
            {
                continue;
            }

            tabs.TabItems.RemoveAt(current);
            tabs.TabItems.Insert(target, desired[target]);
        }
    }
}
