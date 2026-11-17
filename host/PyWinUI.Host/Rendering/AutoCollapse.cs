using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace PyWinUI.Host.Rendering;

/// <summary>
/// 「悬停展开 / 选中保持 / 失焦收起」的单一实现。
///
/// 这项能力原先有两份：宿主这里一份完整的，Python 侧一份手搓动画。
/// Python 那份因为构造参数没写进 props 而成为唯一真正生效的，同时它带着
/// 「收起后延时清字」的竞态。现在统一到这里：
///
/// - 宽度动画按用户指定的公式走，100 步、每步 <c>w += (end - w) / 15</c>、
///   最后一步直接取目标值（(2/3)^20 还剩 0.03% 的零头，不 snap 永远差一点）。
/// - 收起只切文字 <c>Visibility</c>，不动 <c>ItemsSource</c>，避免换源清掉选中项。
/// - 指针事件用 <c>AddHandler(handledEventsToo: true)</c>：ListViewItem 的模板
///   会把指针事件标记为已处理，普通 <c>+=</c> 收不到。
/// - 每次动画都有代次，旧 timer 一律停掉，快速反复悬停不会抖动。
/// </summary>
internal static class AutoCollapse
{
    /// <summary>
    /// 100 步 × 3.2ms = 320ms，与原来 20 步 × 16ms 的总时长一致。
    /// 每步位移是 <c>(end - w) / 15</c>，也就是原来 <c>/ 3</c> 的五分之一：
    /// 采样点密 5 倍，曲线形状不变。
    /// </summary>
    private const int AnimSteps = 100;
    private const double AnimIntervalMs = 3.2;

    /// <summary>每步走剩余距离的 1/15（原先是 1/3）。</summary>
    private const double AnimDivisor = 15.0;

    private sealed class State
    {
        public double Collapsed = 40;
        public double Expanded;
        public bool Hooked;
        public bool PointerOver;
        public bool Pinned;
        public bool Open;
        public bool HoverExpand = true;
        public bool PinOnSelect = true;

        public int Generation;

        /// <summary>事件回传需要节点 id 与 emit；Attach 时注入一次。</summary>
        public string NodeId = "";
        public Action<string, string, JsonObject?, bool>? Emit;
    }

    private static readonly ConditionalWeakTable<FrameworkElement, State> States = new();

    // ==================================================================
    // 挂载
    // ==================================================================

    public static void Attach(
        FrameworkElement element,
        JsonObject props,
        string nodeId,
        Action<string> warn,
        Action<string, string, JsonObject?, bool> emit)
    {
        var collapsed = ValueParsers.AsDouble(props["collapsedWidth"]) ?? 40;
        var expanded = ValueParsers.AsDouble(props["expandedWidth"])
                       ?? (double.IsNaN(element.Width) ? element.ActualWidth : element.Width);

        if (expanded <= collapsed)
        {
            warn($"autoCollapse 的展开宽度 {expanded} 必须大于收起宽度 {collapsed}");
            return;
        }

        if (!States.TryGetValue(element, out var state))
        {
            state = new State();
            States.Add(element, state);
        }

        state.Collapsed = collapsed;
        state.Expanded = expanded;
        state.NodeId = nodeId;
        state.Emit = emit;
        state.HoverExpand = ValueParsers.AsBool(props["hoverExpand"]) ?? true;
        state.PinOnSelect = ValueParsers.AsBool(props["pinOnSelect"]) ?? true;

        if (!state.Hooked)
        {
            state.Hooked = true;

            element.AddHandler(
                UIElement.PointerEnteredEvent,
                new PointerEventHandler((_, _) =>
                {
                    state.PointerOver = true;
                    if (state.HoverExpand)
                    {
                        SetOpen(element, state, true, notify: true);
                    }
                }),
                handledEventsToo: true);

            element.AddHandler(
                UIElement.PointerExitedEvent,
                new PointerEventHandler((_, _) =>
                {
                    state.PointerOver = false;
                    ScheduleCollapse(element, state);
                }),
                handledEventsToo: true);

            // 只订阅 LostFocus，不订阅 GotFocus。
            //
            // 窗口首次显示时焦点会自动落在列表上，GotFocus 会把侧栏展开；
            // 而 SetCompactItems 改 Visibility 又引起焦点抖动，形成
            // 「展开→收起→展开」的循环，动画代次被反复推进，动画永远走不完。
            // 用户要的「点击保持展开」由 PointerEntered + SelectionChanged pin 覆盖，
            // 焦点在内部时由 ScheduleCollapse 的 HasFocusWithin 兜住。
            element.LostFocus += (_, _) => ScheduleCollapse(element, state);

            if (element is ListView list)
            {
                list.SelectionChanged += (_, _) =>
                {
                    if (state.PinOnSelect)
                    {
                        state.Pinned = true;
                    }
                };
            }
        }

        // 初始状态：open 缺省为「收起」
        var open = ValueParsers.AsBool(props["open"]) ?? false;
        state.Open = open;
        state.Pinned = false;
        element.Width = open ? expanded : collapsed;
        SetCompactItems(element, !open);

        HostLog.Write($"[props] autoCollapse {nodeId}: {collapsed} / {expanded} open={open}");
    }

    // ==================================================================
    // 外部命令
    // ==================================================================

    /// <summary>显式展开/收起。供 Python 的 toggle() / set_open() 使用。</summary>
    public static bool SetOpen(FrameworkElement element, bool open)
    {
        if (!States.TryGetValue(element, out var state))
        {
            return false;
        }

        SetOpen(element, state, open, notify: true);
        return true;
    }

    /// <summary>取消「选中保持」并收起。对应 Python 的 release()。</summary>
    public static bool Release(FrameworkElement element)
    {
        if (!States.TryGetValue(element, out var state))
        {
            return false;
        }

        state.Pinned = false;
        SetOpen(element, state, false, notify: true);
        return true;
    }

    public static bool? IsOpen(FrameworkElement element) =>
        States.TryGetValue(element, out var state) ? state.Open : null;

    /// <summary>
    /// items 被替换后重新应用一次文字显隐。
    /// 换 ItemsSource 会重建列表项，新项默认是「文字可见」的。
    /// </summary>
    public static void Refresh(FrameworkElement element)
    {
        if (States.TryGetValue(element, out var state))
        {
            SetCompactItems(element, !state.Open);
        }
    }

    public static bool IsPinned(FrameworkElement element) =>
        States.TryGetValue(element, out var state) && state.Pinned;

    // ==================================================================
    // 状态机
    // ==================================================================

    private static void SetOpen(
        FrameworkElement element,
        State state,
        bool open,
        bool notify)
    {
        // 状态没变就别重启动画。
        // SetCompactItems 改 Visibility 会让 ListView 重新布局，焦点抖动又会触发
        // GotFocus -> SetOpen(true)；不在这里挡住的话，动画代次被反复推进，
        // 动画永远走不完，宽度停在中间。
        if (state.Open == open)
        {
            return;
        }

        state.Open = open;

        if (open)
        {
            // 展开时先放文字，动画过程中标题就是完整的
            SetCompactItems(element, false);
            AnimateWidth(element, state, state.Expanded);
        }
        else
        {
            // 收起时等宽度走完再藏文字：立刻藏会让文字先消失、宽度后收，看着很跳
            AnimateWidth(element, state, state.Collapsed, compactAtEnd: true);
        }

        if (notify)
        {
            state.Emit?.Invoke(state.NodeId, "openChanged", new JsonObject
            {
                ["open"] = open,
                ["pinned"] = state.Pinned,
            }, false);
        }
    }

    /// <summary>
    /// 收起前再确认一次：指针已移开、焦点也不在里面、没有被选中钉住。
    ///
    /// 判断延到下一轮事件循环，是因为焦点从 A 移到 B 会「先 LostFocus 再 GotFocus」，
    /// 当场判断会把刚点进去的面板又收起来。
    /// </summary>
    private static void ScheduleCollapse(FrameworkElement element, State state)
    {
        element.DispatcherQueue.TryEnqueue(() =>
        {
            if (state.PointerOver || state.Pinned || HasFocusWithin(element))
            {
                return;
            }

            SetOpen(element, state, false, notify: true);
        });
    }

    private static bool HasFocusWithin(FrameworkElement element)
    {
        if (element.XamlRoot is null
            || FocusManager.GetFocusedElement(element.XamlRoot) is not DependencyObject focused)
        {
            return false;
        }

        for (var node = focused; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (ReferenceEquals(node, element))
            {
                return true;
            }
        }

        return false;
    }

    // ==================================================================
    // 宽度动画：100 步指数缓出
    // ==================================================================

    /// <summary>
    /// 100 步指数缓出：<c>w += (end - w) / 15</c>，每步 3.2ms，最后一步直接取目标。
    ///
    /// 3.2ms 低于 Windows 默认的 15.6ms 定时器精度，所以不能「循环 100 次各等 3.2ms」——
    /// 那样总时长会变成 1.5 秒。这里用 Stopwatch 记真实时间，每次唤醒按
    /// <c>elapsed / 3.2</c> 算出应该走到第几步，再用闭式解一次算到位：
    /// 走 n 步后的宽度 = <c>end - (end - start) * (14/15)^n</c>。
    ///
    /// 效果是：总时长严格 320ms；60Hz 屏幕上每帧推进约 5 步，数值曲线与
    /// 逐步计算完全一致；120Hz/144Hz 屏幕上能看到更多中间帧。
    /// </summary>
    private static async void AnimateWidth(
        FrameworkElement element,
        State state,
        double target,
        bool compactAtEnd = false)
    {
        // 新一代开始，旧循环下一次检查时自行退出
        state.Generation++;
        var generation = state.Generation;

        var start = double.IsNaN(element.Width) ? element.ActualWidth : element.Width;
        if (Math.Abs(target - start) < 0.5)
        {
            element.Width = target;
            if (compactAtEnd)
            {
                SetCompactItems(element, true);
            }

            return;
        }

        try
        {
            var clock = Stopwatch.StartNew();
            var lastStep = 0;

            // 16ms 是 Windows 上唯一稳定的调度粒度。3.2ms 的 Task.Delay
            // 会被调度器合批/降优先级，实测 350ms 只走了 4 步。
            // 所以这里每次唤醒按真实经过时间换算步数，一次补齐约 5 步：
            // 数值曲线仍是 100 步的采样，但唤醒次数只有 20 次。
            while (lastStep < AnimSteps)
            {
                await Task.Delay(16);

                if (generation != state.Generation)
                {
                    return;
                }

                var elapsed = clock.Elapsed.TotalMilliseconds;
                var step = Math.Min(AnimSteps, (int)(elapsed / AnimIntervalMs));
                if (step <= lastStep)
                {
                    continue;
                }

                lastStep = step;

                if (step >= AnimSteps)
                {
                    break;
                }

                var value = target
                    - (target - start) * Math.Pow(1.0 - 1.0 / AnimDivisor, step);
                element.Width = Math.Round(value, 1);
            }

            if (generation != state.Generation)
            {
                return;
            }

            // 布局可能把最后一步的 200 舍成 199.7，收尾再钉一次目标值
            element.Width = target;

            // 收起时文字留到动画结束再藏，避免宽度还没动文字就先消失
            if (compactAtEnd)
            {
                SetCompactItems(element, true);
            }
        }
        catch (Exception ex)
        {
            // async void 里异常逃逸会带走进程，这里必须兜住
            HostLog.Write("[autocollapse] 动画失败: " + ex.Message);
            element.Width = target;
            if (compactAtEnd)
            {
                SetCompactItems(element, true);
            }
        }
    }

    /// <summary>
    /// 收起时把列表项的说明文字藏掉，只留图标。
    ///
    /// 只对富列表项生效（宿主把 {"text","glyph"} 构造成 StackPanel{Icon, TextBlock}）；
    /// 纯字符串项没有可藏的部分，会按宽度被裁掉。
    /// </summary>
    private static void SetCompactItems(FrameworkElement element, bool compact)
    {
        if (element is not ListView list)
        {
            return;
        }

        foreach (var item in list.Items)
        {
            if (item is not Panel panel)
            {
                continue;
            }

            foreach (var child in panel.Children)
            {
                if (child is TextBlock block)
                {
                    block.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
                }
            }
        }
    }
}
