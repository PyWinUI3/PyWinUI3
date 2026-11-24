using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using WinRT.Interop;
using PyWinUI.Host.Protocol;

namespace PyWinUI.Host.Rendering;

/// <summary>
/// 把一扇已经 Activate 过的 WinUI 窗口降级成另一扇窗口的 WS_CHILD 子窗口。
///
/// 为什么先 Activate 再改样式：XamlRoot 在 Activate 之前为 null，
/// 子窗口里的控件首帧会失败。所以顺序是「先当正常窗口亮一下，再塞进父窗口」。
///
/// 关于标题栏——这里**不**保留 WS_CAPTION：
/// 子窗口即使带着 WS_CAPTION，系统也只画一圈边框，不画标题文字和按钮，
/// 非客户区白白占掉 39px，看上去就是一条空白。所以标题栏由
/// <c>FormRenderer.WrapForEmbedded</c> 用 XAML 自绘，样式这边保持无边框无标题栏。
/// </summary>
internal static class EmbeddedWindow
{
    private const int GWL_STYLE = -16;
    private const int GWL_EXSTYLE = -20;

    private const long WS_CHILD = 0x40000000L;
    private const long WS_POPUP = 0x80000000L;
    private const long WS_CAPTION = 0x00C00000L;
    private const long WS_THICKFRAME = 0x00040000L;
    private const long WS_MINIMIZEBOX = 0x00020000L;
    private const long WS_MAXIMIZEBOX = 0x00010000L;
    private const long WS_SYSMENU = 0x00080000L;
    private const long WS_BORDER = 0x00800000L;
    private const long WS_DLGFRAME = 0x00400000L;
    private const long WS_VISIBLE = 0x10000000L;
    private const long WS_CLIPCHILDREN = 0x02000000L;
    private const long WS_CLIPSIBLINGS = 0x04000000L;

    private const long WS_EX_APPWINDOW = 0x00040000L;
    private const long WS_EX_DLGMODALFRAME = 0x00000001L;

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_FRAMECHANGED = 0x0020;
    private const uint SWP_SHOWWINDOW = 0x0040;

    private const uint WM_NCLBUTTONDOWN = 0x00A1;

    /// <summary>命中测试码：标题栏。发给 WM_NCLBUTTONDOWN 等于"按在标题栏上"。</summary>
    private const int HTCAPTION = 2;

    private static readonly IntPtr HWND_TOP = IntPtr.Zero;

    /// <summary>
    /// 每个父窗口下的子窗口，按加入顺序记录。
    ///
    /// 用来给 lowest 层找锚点：HWND_BOTTOM 会把子窗口压到**父窗口内容**之下
    /// （DWM 合成下 XAML 内容和子 HWND 在同一套 z 序里），整扇窗就被内容盖住、
    /// 看起来像没显示。用「第一个兄弟句柄」做插入锚点，才是真正的
    /// 「在同级子窗口里垫底，但仍高于父窗口内容」。
    /// </summary>
    private static readonly Dictionary<IntPtr, List<IntPtr>> Siblings = new();

    /// <summary>
    /// 每扇窗口当前的标题栏高度。渲染层在开关返回按钮/输入框时更新，
    /// 子窗口据此决定「再往上就压到父窗口标题栏」的禁区。
    /// 默认 32，和 WindowChrome 里的紧凑高度一致。
    /// </summary>
    private static readonly Dictionary<IntPtr, int> TitleBarHeights = new();

    /// <summary>由渲染层登记窗口的标题栏高度。</summary>
    public static void SetTitleBarHeight(IntPtr hwnd, int height)
    {
        if (hwnd != IntPtr.Zero)
        {
            TitleBarHeights[hwnd] = Math.Max(0, height);
        }
    }

    /// <summary>读一扇窗口的标题栏高度，没登记过就用默认紧凑高度。</summary>
    private static int TitleBarHeightOf(IntPtr hwnd) =>
        hwnd != IntPtr.Zero && TitleBarHeights.TryGetValue(hwnd, out var height)
            ? height
            : WindowChrome.TitleBarHeight;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    // ---- 折叠（最小化到底部）----

    private const int CollapsedWidth = 220;
    private const int CollapsedHeight = 32;

    /// <summary>展开状态下要恢复的几何，坐标相对父窗口客户区。</summary>
    private static readonly Dictionary<IntPtr, (int X, int Y, int W, int H)> SavedRects = new();

    private static readonly HashSet<IntPtr> CollapsedWindows = new();

    /// <summary>已经被最大化（填满父客户区）的子窗口。</summary>
    private static readonly HashSet<IntPtr> MaximizedWindows = new();

    /// <summary>这扇子窗口当前是否最大化。</summary>
    public static bool IsMaximized(Window window)
    {
        var hwnd = WindowNative.GetWindowHandle(window);
        return hwnd != IntPtr.Zero && MaximizedWindows.Contains(hwnd);
    }

    /// <summary>
    /// 最大化 / 还原。
    ///
    /// 子窗口的"最大化"是填满父窗口客户区（不是屏幕），这样才符合"嵌在别人里面"
    /// 的语义。还原回到最大化前的几何。
    /// </summary>
    public static void ToggleMaximize(Window child, Window parent)
    {
        var hwnd = WindowNative.GetWindowHandle(child);
        var parentHwnd = WindowNative.GetWindowHandle(parent);
        if (hwnd == IntPtr.Zero || parentHwnd == IntPtr.Zero)
        {
            return;
        }

        GetClientRect(parentHwnd, out var client);

        if (MaximizedWindows.Remove(hwnd))
        {
            if (SavedRects.TryGetValue(hwnd, out var saved))
            {
                SetWindowPos(hwnd, HWND_TOP, saved.X, saved.Y, saved.W, saved.H,
                    SWP_FRAMECHANGED | SWP_SHOWWINDOW | SWP_NOACTIVATE);
            }

            HostLog.Write("[embed] restored");
            return;
        }

        // 折叠状态下先展开，否则记下来的几何是那条 220x32 的条
        if (CollapsedWindows.Remove(hwnd))
        {
            HostLog.Write("[embed] expand before maximize");
        }

        GetWindowRect(hwnd, out var screen);
        var origin = new POINT { X = screen.Left, Y = screen.Top };
        ScreenToClient(parentHwnd, ref origin);
        SavedRects[hwnd] = (
            origin.X, origin.Y,
            screen.Right - screen.Left, screen.Bottom - screen.Top);

        SetWindowPos(hwnd, HWND_TOP, 0, 0,
            Math.Max(1, client.Right - client.Left),
            Math.Max(1, client.Bottom - client.Top),
            SWP_FRAMECHANGED | SWP_SHOWWINDOW | SWP_NOACTIVATE);

        MaximizedWindows.Add(hwnd);
        HostLog.Write("[embed] maximized to parent client area");
    }

    /// <summary>这扇子窗口当前是否处于折叠状态（标题栏图标要据此切换成「还原」）。</summary>
    public static bool IsCollapsed(Window window)
    {
        var hwnd = WindowNative.GetWindowHandle(window);
        return hwnd != IntPtr.Zero && CollapsedWindows.Contains(hwnd);
    }

    /// <summary>
    /// 折叠 / 展开。
    ///
    /// 折叠后缩成一条 220x32 的标题条，贴在父窗口客户区**左下角**，
    /// 并置于 z 序最上（HWND_TOP）——否则同父的其它子窗口会把它盖住。
    /// 再点一次还原到折叠前的位置和尺寸。
    /// </summary>
    public static void ToggleCollapse(Window child, Window parent)
    {
        var hwnd = WindowNative.GetWindowHandle(child);
        var parentHwnd = WindowNative.GetWindowHandle(parent);
        if (hwnd == IntPtr.Zero || parentHwnd == IntPtr.Zero)
        {
            return;
        }

        GetClientRect(parentHwnd, out var client);
        var clientHeight = Math.Max(1, client.Bottom - client.Top);

        if (CollapsedWindows.Remove(hwnd))
        {
            if (SavedRects.TryGetValue(hwnd, out var saved))
            {
                SetWindowPos(hwnd, HWND_TOP, saved.X, saved.Y, saved.W, saved.H,
                    SWP_FRAMECHANGED | SWP_SHOWWINDOW | SWP_NOACTIVATE);
            }

            HostLog.Write("[embed] expanded");
            return;
        }

        // 记下当前几何（屏幕坐标 -> 父客户区坐标）
        GetWindowRect(hwnd, out var screen);
        var origin = new POINT { X = screen.Left, Y = screen.Top };
        ScreenToClient(parentHwnd, ref origin);
        SavedRects[hwnd] = (
            origin.X, origin.Y,
            screen.Right - screen.Left, screen.Bottom - screen.Top);

        // 折叠成的小条落在父窗口可见区域的左下角。
        var trim = ParentTrim(parentHwnd);

        SetWindowPos(hwnd, HWND_TOP, trim, clientHeight - CollapsedHeight,
            CollapsedWidth, CollapsedHeight,
            SWP_FRAMECHANGED | SWP_SHOWWINDOW | SWP_NOACTIVATE);

        CollapsedWindows.Add(hwnd);
        HostLog.Write("[embed] collapsed to bottom-left");
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    /// <summary>SW_RESTORE：从最大化还原成正常大小。</summary>
    private const int SW_RESTORE = 9;

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(
        int x1, int y1, int x2, int y2, int widthEllipse, int heightEllipse);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("kernel32.dll")]
    private static extern void SetLastError(uint dwErrCode);

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    // ---- 手动拖动 ----
    //
    // 不用系统移动循环（WM_NCLBUTTONDOWN/HTCAPTION 或 SC_MOVE）：那套要求调用时
    // 左键确实处于按下状态，而 XAML 的 PointerPressed 把控制权交给系统的时机
    // 总是差一拍，结果就是「松手后才进入可移动状态、再点一次才退出」。
    // 这里改成指针事件逐帧驱动：按下即跟手、松手即停，光标也保持默认箭头。

    private struct DragOrigin
    {
        public int CursorX;
        public int CursorY;
        public int WindowX;
        public int WindowY;
    }

    private static readonly Dictionary<IntPtr, DragOrigin> DragOrigins = new();

    /// <summary>开始拖动：记下光标与窗口的屏幕坐标作为基准。</summary>
    /// <summary>
    /// 开始拖动。
    ///
    /// <paramref name="pointerXInTitleBar"/> / <paramref name="pointerYInTitleBar"/>
    /// 是指针按下时**在标题栏元素内**的位置。从最大化还原时用它把标题栏
    /// 对回鼠标底下——指针按在标题栏哪里，还原后还在哪里。
    /// </summary>
    public static void BeginDrag(Window window, double pointerXInTitleBar, double pointerYInTitleBar)
    {
        var hwnd = WindowNative.GetWindowHandle(window);
        if (hwnd == IntPtr.Zero || !GetCursorPos(out var cursor))
        {
            return;
        }

        // 最大化状态下拖标题栏 = 先还原（Windows 的标准行为）。
        //
        // 还原后必须把窗口挪到光标处：否则窗口会弹回最大化前的位置，
        // 而光标还停在屏幕中间——接着一拖，窗口就「跳」了。
        RestoreForDrag(window, hwnd, cursor, pointerXInTitleBar, pointerYInTitleBar);

        if (!GetWindowRect(hwnd, out var rect))
        {
            return;
        }

        DragOrigins[hwnd] = new DragOrigin
        {
            CursorX = cursor.X,
            CursorY = cursor.Y,
            WindowX = rect.Left,
            WindowY = rect.Top,
        };
    }

    private const long WS_MAXIMIZE = 0x01000000L;

    /// <summary>
    /// 窗口是最大化状态时，先还原，再把窗口挪到光标位置。
    ///
    /// 光标在标题栏里的相对比例保持不变（最大化时标题栏是整个屏幕宽，
    /// 还原后按比例算，光标就还在原来按的那个位置）。
    /// </summary>
    private static void RestoreForDrag(
        Window window,
        IntPtr hwnd,
        POINT cursor,
        double pointerXInTitleBar,
        double pointerYInTitleBar)
    {
        if ((GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64() & WS_MAXIMIZE) == 0)
        {
            return;
        }

        // 用 ShowWindow(SW_RESTORE) 而不是 presenter.Restore()。
        //
        // presenter.Restore() 是走框架的异步还原：它会把窗口放回自己保存的
        // maximized 位置，我们随后设的位置会被顶掉——表现是「无论在标题栏
        // 哪儿拖动，还原后光标都落在标题栏的同一个位置」（固定偏移，跟拖动点无关）。
        //
        // ShowWindow 是同步的，返回时窗口已经还原完，后面的定位就是最后生效的。
        ShowWindow(hwnd, SW_RESTORE);

        if (!GetWindowRect(hwnd, out _))
        {
            return;
        }

        // 还原后标题栏**不在窗口顶部**：外面还有 OuterTrim 的裁剪
        // 和 TransparentMargin 的透明边。不把这层算进去，光标就会落在
        // 那圈透明 padding 上，而不是标题栏上。
        var margin = WindowChrome.OuterTrim + WindowChrome.TransparentMargin;

        // 把标题栏上「指针按下的那个点」对回鼠标当前位置：
        // 指针按在标题栏哪里，还原后就在哪里。
        var x = (int)(cursor.X - margin - pointerXInTitleBar);
        var y = (int)(cursor.Y - margin - pointerYInTitleBar);

        MoveTo(hwnd, x, y);

        // presenter.Restore() 之后 WinUI 还会按自己保存的 maximized 位置
        // 再摆一次窗口，把上面那次 SetWindowPos 顶掉——表现就是
        // 「还原后窗口偏上，光标落在标题栏下面」。
        // 所以布局稳定后再摆一次，确保最终位置是我们要的。
        var queue = window.DispatcherQueue;
        if (queue is not null)
        {
            queue.TryEnqueue(() =>
            {
                Task.Delay(120).ContinueWith(_ =>
                {
                    queue.TryEnqueue(() => MoveTo(hwnd, x, y));
                });
            });
        }
    }

    /// <summary>只挪位置，不改大小、不抢 Z 序、不抢焦点。</summary>
    private static void MoveTo(IntPtr hwnd, int x, int y)
    {
        SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0,
            SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    /// <summary>
    /// 父窗口最外圈被裁掉了多少。
    ///
    /// 父窗口用 SetWindowRgn 裁掉最外 <see cref="WindowChrome.OuterTrim"/> 像素
    /// （用来遮 DWM 残留的半透明脏边）。子窗口是父 HWND 的子窗口，坐标是
    /// **父客户区**坐标，所以它落在 (0,0) 的那部分正好在被裁掉的区域里——
    /// 子窗口的边框会缺一条。
    ///
    /// 这个值就是「可见区域相对客户区左上角的偏移」，子窗口的定位和
    /// 拖动范围都要拿它补偿：补偿之后，子窗口能拖到的范围正好等于
    /// 父窗口的**可见区域**，也就是贴到窗口边缘。
    ///
    /// 父窗口最大化时不裁（整块屏幕都是内容），偏移为 0。
    /// </summary>
    private static int ParentTrim(IntPtr parentHwnd)
    {
        const long WS_MAXIMIZE = 0x01000000L;
        if ((GetWindowLongPtr(parentHwnd, GWL_STYLE).ToInt64() & WS_MAXIMIZE) != 0)
        {
            return 0;
        }

        return WindowChrome.OuterTrim;
    }

    /// <summary>拖动中：把窗口挪到「起始位置 + 光标位移」处（换算回父窗口客户区）。</summary>
    public static void UpdateDrag(Window window)
    {
        var hwnd = WindowNative.GetWindowHandle(window);
        if (hwnd == IntPtr.Zero || !DragOrigins.TryGetValue(hwnd, out var origin))
        {
            return;
        }

        if (!GetCursorPos(out var cursor))
        {
            return;
        }

        var target = new POINT
        {
            X = origin.WindowX + (cursor.X - origin.CursorX),
            Y = origin.WindowY + (cursor.Y - origin.CursorY),
        };

        // 子窗口的坐标是相对父客户区的，屏幕坐标得先换算
        var parentHwnd = GetParent(hwnd);
        if (parentHwnd != IntPtr.Zero)
        {
            ScreenToClient(parentHwnd, ref target);

            // 子窗口的活动范围 = 父窗口的**可见区域**。
            //
            // 父客户区最外一圈被裁掉了（见 ParentTrim），所以先把坐标
            // 换算成「相对可见区域左上角」，在这里限制，最后再换回去——
            // 这样子窗口能一路拖到窗口边缘，而不是缩在一圈内缩里。
            if (GetClientRect(parentHwnd, out var parentClient)
                && GetWindowRect(hwnd, out var childRect))
            {
                var trim = ParentTrim(parentHwnd);

                var childWidth = childRect.Right - childRect.Left;
                var childHeight = childRect.Bottom - childRect.Top;

                var visibleWidth = parentClient.Right - parentClient.Left - trim * 2;
                var visibleHeight = parentClient.Bottom - parentClient.Top - trim * 2;

                var maxX = Math.Max(0, visibleWidth - childWidth);
                var maxY = Math.Max(0, visibleHeight - childHeight);

                // 上边缘不给到 0：父窗口的标题栏占着最上面一条，
                // 子窗口爬上去会压住标题栏（标题、按钮都在那儿）。
                var topLimit = TitleBarHeightOf(parentHwnd);

                target.X = Math.Clamp(target.X - trim, 0, maxX) + trim;
                target.Y = Math.Clamp(target.Y - trim, topLimit, maxY) + trim;
            }
        }

        SetWindowPos(hwnd, IntPtr.Zero, target.X, target.Y, 0, 0,
            SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    /// <summary>结束拖动，丢掉基准。</summary>
    public static void EndDrag(Window window)
    {
        var hwnd = WindowNative.GetWindowHandle(window);
        if (hwnd != IntPtr.Zero)
        {
            DragOrigins.Remove(hwnd);
        }
    }

    /// <summary>
    /// 让窗口跟随鼠标移动，直到松开左键。
    ///
    /// 用的是"按在标题栏上"这套（WM_NCLBUTTONDOWN + HTCAPTION），而不是
    /// WM_SYSCOMMAND/SC_MOVE。区别在光标：SC_MOVE 会把指针换成四向移动箭头，
    /// 拖标题栏则保持默认箭头——这才是用户期望的手感。
    ///
    /// 注意：这个调用会**阻塞**到移动结束（系统移动循环自带消息泵），
    /// 所以调用方必须在 PointerPressed 返回之后再执行，否则 XAML 的指针状态
    /// 还没结算完就进了模态循环，时序会错成「按下->松开->才动」。
    /// 见 FormRenderer 里的 TryEnqueue。
    /// </summary>
    public static void StartMove(Window window)
    {
        var hwnd = WindowNative.GetWindowHandle(window);
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        ReleaseCapture();
        SendMessage(hwnd, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
    }

    /// <summary>
    /// 把 <paramref name="child"/> 嵌进 <paramref name="parent"/> 的客户区。
    /// 失败时通过 <paramref name="warn"/> 回传原因，窗口会退化成普通顶层窗口继续可用。
    /// </summary>
    public static bool Attach(Window child, Window parent, WindowSpec spec, Action<string> warn)
    {
        var childHwnd = WindowNative.GetWindowHandle(child);
        var parentHwnd = WindowNative.GetWindowHandle(parent);

        if (childHwnd == IntPtr.Zero || parentHwnd == IntPtr.Zero)
        {
            warn("嵌入失败：拿不到窗口句柄");
            return false;
        }

        // 1. 顶层弹出窗 -> 无边框子窗口。
        //    标题栏和外框都由 XAML 自绘（见 FormRenderer.WrapForEmbedded），
        //    这里把所有边框类样式清干净，避免和自绘的那层叠在一起。
        var style = GetWindowLongPtr(childHwnd, GWL_STYLE).ToInt64();
        style &= ~(WS_POPUP | WS_CAPTION | WS_THICKFRAME | WS_MINIMIZEBOX
                   | WS_MAXIMIZEBOX | WS_SYSMENU | WS_BORDER | WS_DLGFRAME);
        style |= WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN | WS_CLIPSIBLINGS;
        SetWindowLongPtr(childHwnd, GWL_STYLE, new IntPtr(style));

        // 扩展样式里的 APPWINDOW 会给它单开一个任务栏按钮，必须去掉
        var exStyle = GetWindowLongPtr(childHwnd, GWL_EXSTYLE).ToInt64();
        exStyle &= ~(WS_EX_APPWINDOW | WS_EX_DLGMODALFRAME);
        SetWindowLongPtr(childHwnd, GWL_EXSTYLE, new IntPtr(exStyle));

        // 1.5 子窗口也做圆角：客户区归零 + 不擦背景，
        //     圆角外保留父窗口已画的像素，视觉上与父窗口融为一体。
        CustomChrome.EnableEmbedded(childHwnd);

        // 2. 换父。SetParent 失败时返回 NULL，且 LastError 非 0
        SetLastError(0);
        SetParent(childHwnd, parentHwnd);
        var error = Marshal.GetLastWin32Error();
        if (error != 0)
        {
            warn($"嵌入失败：SetParent 返回错误 {error}");
            return false;
        }

        var scale = GetDpiForWindow(parentHwnd) / 96.0;
        if (scale <= 0)
        {
            scale = 1.0;
        }

        GetClientRect(parentHwnd, out var client);
        var (relX, relY, relW, relH) = Ratios(spec, client, scale);
        Apply(childHwnd, parentHwnd, spec.Layer, client, relX, relY, relW, relH);

        // 3. 父窗口缩放时按比例跟随。子窗口是相对父客户区定位的，
        //    父窗口平移不用管（系统自动跟随），尺寸变化必须自己算。
        parent.SizeChanged += (_, _) =>
        {
            if (!IsWindow(childHwnd))
            {
                return;
            }

            GetClientRect(parentHwnd, out var now);

            // 折叠中的子窗口永远贴着左下角，不参与比例缩放
            if (CollapsedWindows.Contains(childHwnd))
            {
                var clientHeight = Math.Max(1, now.Bottom - now.Top);
                SetWindowPos(childHwnd, HWND_TOP, 0, clientHeight - CollapsedHeight,
                    CollapsedWidth, CollapsedHeight,
                    SWP_FRAMECHANGED | SWP_SHOWWINDOW | SWP_NOACTIVATE);
                return;
            }

            Apply(childHwnd, parentHwnd, spec.Layer, now, relX, relY, relW, relH);
        };

        HostLog.Write($"[embed] {child.Title} -> parent {parentHwnd}, layer={spec.Layer ?? "highest"}");
        return true;
    }

    /// <summary>
    /// 算出子窗口相对父客户区的比例。显式宽高按逻辑像素换算，没给就占 80% 居中。
    /// 用比例而不是绝对像素，是为了父窗口缩放时能等比跟随。
    /// </summary>
    private static (double X, double Y, double W, double H) Ratios(WindowSpec spec, RECT client, double scale)
    {
        var cw = Math.Max(1.0, client.Right - client.Left);
        var ch = Math.Max(1.0, client.Bottom - client.Top);

        if (spec.Width is { } width && spec.Height is { } height)
        {
            var pw = Math.Min(cw, width * scale);
            var ph = Math.Min(ch, height * scale);
            var px = spec.X is { } x ? x * scale : (cw - pw) / 2;
            var py = spec.Y is { } y ? y * scale : (ch - ph) / 2;

            // 负坐标在子窗口里没有意义，夹回客户区
            px = Math.Clamp(px, 0, Math.Max(0, cw - pw));
            py = Math.Clamp(py, 0, Math.Max(0, ch - ph));
            return (px / cw, py / ch, pw / cw, ph / ch);
        }

        return (0.1, 0.1, 0.8, 0.8);
    }

    private static void Apply(
        IntPtr childHwnd, IntPtr parentHwnd, string? layer, RECT client,
        double relX, double relY, double relW, double relH)
    {
        var cw = Math.Max(1.0, client.Right - client.Left);
        var ch = Math.Max(1.0, client.Bottom - client.Top);

        var x = (int)Math.Round(relX * cw);
        var y = (int)Math.Round(relY * ch);
        var w = Math.Max(1, (int)Math.Round(relW * cw));
        var h = Math.Max(1, (int)Math.Round(relH * ch));

        if (!Siblings.TryGetValue(parentHwnd, out var siblings))
        {
            siblings = new List<IntPtr>();
            Siblings[parentHwnd] = siblings;
        }

        if (!siblings.Contains(childHwnd))
        {
            siblings.Add(childHwnd);
        }

        var insertAfter = HWND_TOP;
        if (string.Equals(layer, "lowest", StringComparison.OrdinalIgnoreCase))
        {
            // 垫在第一个兄弟下面，而不是 HWND_BOTTOM（那会被父窗口内容盖住）
            var anchor = siblings.FirstOrDefault(h => h != childHwnd);
            if (anchor != IntPtr.Zero)
            {
                insertAfter = anchor;
            }
        }

        // 父客户区最外一圈被裁掉了，子窗口要往里挪那么多，
        // 它的边框才落在可见区域的边缘上，而不是缺一条。
        var trim = ParentTrim(parentHwnd);

        // 上边缘同样不让到 0——别把子窗口摆在父窗口的标题栏上。
        var top = Math.Max(y, TitleBarHeightOf(parentHwnd));

        SetWindowPos(childHwnd, insertAfter, x + trim, top + trim, w, h,
            SWP_FRAMECHANGED | SWP_SHOWWINDOW | SWP_NOACTIVATE);

        // 圆角**只**由 XAML 侧负责（Composition 的几何裁剪），这里不再用
        // SetWindowRgn 去裁窗口形状。
        //
        // 两套裁剪叠在一起会出问题：Win32 那套只把**窗口形状**裁成圆角，
        // 里面 XAML 画的白底仍是矩形，折叠成小条时尤其明显——线框是圆的，
        // 背后却糊着一块矩形白底。统一交给 XAML 之后，角上的像素由
        // Composition 裁掉，抗锯齿也更好。
    }
}
