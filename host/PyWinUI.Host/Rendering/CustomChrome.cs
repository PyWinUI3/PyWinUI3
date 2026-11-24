using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace PyWinUI.Host.Rendering;

/// <summary>
/// 顶层窗口的外观：无边框 + 透明背景 + 圆角。
///
/// 分工：
/// - **无边框**归这里（框架的 SetBorderAndTitleBar + Win32 样式位 + 客户区归零）；
/// - **透明**归 WinUIEx 的 <c>TransparentTintBackdrop</c>（它接管扩展帧、
///   blur behind、背景擦除、以及 DWM 合成状态变化后的重做）；
/// - **圆角**归渲染层（对窗口外壳做圆角裁剪）。
///
/// 之前这里自己实现过一整套透明逻辑（DWM 玻璃 + SystemBackdrop 手动赋值 +
/// 焦点变化时重挂），焦点来回切总会失效——挂载时机全靠延时去猜。
/// 交给库之后时机由框架保证，稳定得多。
/// </summary>
internal static class CustomChrome
{
    private const int GWL_STYLE = -16;

    private const long WS_CAPTION = 0x00C00000L;
    private const long WS_SYSMENU = 0x00080000L;
    private const long WS_MINIMIZEBOX = 0x00020000L;
    private const long WS_MAXIMIZEBOX = 0x00010000L;
    private const long WS_THICKFRAME = 0x00040000L;

    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_FRAMECHANGED = 0x0020;
    private const uint SWP_SHOWWINDOW = 0x0040;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    /// <summary>
    /// 挂透明 backdrop。**必须在窗口 Activate 之前调用**。
    ///
    /// WinUIEx 的 TransparentTintBackdrop 在 OnTargetConnected 里接管
    /// DWM 扩展帧、blur behind 和背景擦除。挂晚了，框架已经激活、
    /// 表面已经建好，这套接管就赶不上——窗口外缘会留一圈白色底色。
    /// </summary>
    public static void EnableBackdrop(Window window)
    {
        window.SystemBackdrop = new WinUIEx.TransparentTintBackdrop();
    }

    /// <summary>
    /// 只把根 Visual 的底色换成透明（嵌入子窗口用）。
    ///
    /// 同样必须在 Activate 之前调用。
    /// </summary>
    public static void EnableTransparentBackdrop(Window window)
    {
        window.SystemBackdrop = new TransparentBackdrop();
    }

    /// <summary>
    /// 拿到用来建透明画刷的合成器。
    ///
    /// 走 XAML 元素的 visual 拿 UI 合成器（Microsoft.UI.Composition）：
    /// 它和 SystemBackdrop 要的 CompositionBrush 类型完全对得上，
    /// 也不需要 Windows.System.DispatcherQueue——后者在 WinAppSDK 里
    /// 根本没法创建（Windows.System.DispatcherQueueController
    /// .CreateOnCurrentThread 不在 Windows.Win32 元数据里）。
    /// </summary>
    internal static Microsoft.UI.Composition.Compositor? EnsureSystemCompositor()
    {
        return _uiCompositor;
    }

    private static Microsoft.UI.Composition.Compositor? _uiCompositor;

    /// <summary>由渲染层在窗口外壳建好时登记 UI 合成器。</summary>
    internal static void RegisterUiCompositor(Microsoft.UI.Composition.Compositor? compositor)
    {
        _uiCompositor = compositor;
    }

    private const uint WM_MOVE = 0x0003;

    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    /// <summary>
    /// 强制发一次「原地移动」通知。
    ///
    /// 窗口加载完成、首帧落地时，框架那边的状态往往还没跟上——最大化图标
    /// 是不是该切、圆角该不该去掉、拖动的基准在哪，都是在这之后才定的。
    /// 发一次 WM_MOVE 让它把这些重新算一遍。
    ///
    /// 消息里不带任何实际位移，窗口**不会真的动**，只是通知「位置变了」
    /// （Windows 内部就是这么用它的）。
    /// </summary>
    public static void NotifyMove(Window window)
    {
        var hwnd = WindowNative.GetWindowHandle(window);
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        PostMessage(hwnd, WM_MOVE, IntPtr.Zero, IntPtr.Zero);
    }

    // ---- 系统版本 ----
    //
    // Windows 11 21H2（build 22000）起，DWM 能给窗口画**系统圆角**
    //（DWMWA_WINDOW_CORNER_PREFERENCE = DWMWCP_ROUND）：带抗锯齿，
    // 阴影也正确，比自己裁圆角强得多。
    //
    // Win10 没有这个能力，圆角只能自绘。
    //
    // 用 RtlGetVersion 而不是 Environment.OSVersion：后者在没有
    // supportedOS 清单的桌面应用里会被兼容性 shim 挡成 6.2（Win7），
    // 拿到的版本号是假的。

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RTL_OSVERSIONINFOEX
    {
        public uint dwOSVersionInfoSize;
        public uint dwMajorVersion;
        public uint dwMinorVersion;
        public uint dwBuildNumber;
        public uint dwPlatformId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szCSDVersion;
    }

    [DllImport("ntdll.dll")]
    private static extern int RtlGetVersion(ref RTL_OSVERSIONINFOEX versionInfo);

    /// <summary>是不是 Windows 11（build 22000 及以上）。</summary>
    internal static bool IsWindows11
    {
        get
        {
            var info = new RTL_OSVERSIONINFOEX
            {
                dwOSVersionInfoSize = (uint)Marshal.SizeOf<RTL_OSVERSIONINFOEX>(),
            };

            if (RtlGetVersion(ref info) != 0)
            {
                return false;
            }

            return info.dwMajorVersion > 10 || info.dwBuildNumber >= 22000;
        }
    }

    /// <summary>
    /// 这一扇窗口能不能用系统圆角。
    ///
    /// 只有**顶层窗口**行：DWM 的窗口圆角是按顶层窗口算的，
    /// 嵌入子窗口（WS_CHILD）拿不到，仍要自绘。
    /// </summary>
    internal static bool CanUseSystemCorner(bool embedded) => IsWindows11 && !embedded;

    /// <summary>给顶层窗口套上无边框 + 透明背景。</summary>
    public static void Enable(Window window)
    {
        var hwnd = WindowNative.GetWindowHandle(window);
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        // 1) 用框架认可的方式关掉标题栏和边框。
        //    只改样式位不够：presenter 会在渲染/激活后把样式重设一遍。
        try
        {
            if (window.AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.SetBorderAndTitleBar(hasBorder: false, hasTitleBar: false);
            }
        }
        catch (Exception ex)
        {
            HostLog.Write("[chrome] SetBorderAndTitleBar 失败: " + ex.Message);
        }

        // 1b) 明确声明内容延伸到标题栏。
        //
        // Win11 上只清样式位不够稳：DWM 仍可能按系统标题栏预留一条区域，
        // 表现为自绘标题栏上方多出一条空白，或者输入框/返回按钮点不到。
        // 显式置一次 ExtendsContentIntoTitleBar，保证**所有平台**用的都是
        // FormRenderer 自己画的那条标题栏。
        try
        {
            var titleBar = window.AppWindow?.TitleBar;
            if (titleBar is not null)
            {
                titleBar.ExtendsContentIntoTitleBar = true;
            }
        }
        catch (Exception ex)
        {
            HostLog.Write("[chrome] ExtendsContentIntoTitleBar 失败: " + ex.Message);
        }

        // 2) 再补一刀 Win32 样式位，把系统边框相关的位清干净。
        StripBorderStyles(hwnd);

        // 3) 客户区归零：非客户区不存在，内容铺满整个窗口，
        //    圆角裁剪才作用在窗口边界上而不是缩进一圈。
        HookNonClientCalc(hwnd);

        // 4) 透明背景**不在这里**设——必须在 Activate 之前设，
        //    见 EnableBackdrop。WinUIEx 的 backdrop 在 OnTargetConnected 里
        //    做 DWM 扩展帧和背景擦除，Activate 之后再挂就晚了：
        //    DWM 那层来不及接管，窗口外缘会留一圈白底。

        // 5) 系统圆角 / 描边。
        //    Win11 上让 DWM 自己画圆角（带抗锯齿，阴影也对）；
        //    Win10 没有这个能力，圆角走自绘。
        ApplySystemBorder(hwnd, useSystemCorner: CanUseSystemCorner(embedded: false));

        // 6) DWM 那一层我们自己补上：空区域 blur behind。
        //
        // 注意 margins 用 0（不扩展）：扩展帧交给 WinUIEx 的
        // TransparentTintBackdrop 处理，这里只补 blur behind 让 DWM 不铺底色。
        // 曾经以为要传 -1，实际那样会把玻璃层铺满客户区、反而盖掉 XAML 内容。
        ApplyDwmTransparency(hwnd);

        // 裁掉窗口最外圈的脏边（只对顶层真实窗口，WS_CHILD 子窗口没有这问题）
        ApplyOuterTrim(hwnd);
        window.SizeChanged += (_, _) => ApplyOuterTrim(hwnd);

        // 让 DWM 把这一轮的合成结果立刻提交，别让旧表面残留
        DwmFlush();

        HostLog.Write("[chrome] 无边框 + 透明背景已启用");
    }

    // ---- DWM 层透明 ----
    //
    // DwmExtendFrameIntoClientArea + DwmEnableBlurBehindWindow（配一个**空区域**，
    // 表示不模糊任何像素，但让 DWM 不再绘制窗口背景）。
    //
    // 必须成对使用，缺一个 DWM 就会自己铺一层白色窗口底色——
    // 表现是「圆角是圆的，但圆角外还有一圈白」。

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DwmBlurBehind
    {
        public uint Flags;
        [MarshalAs(UnmanagedType.Bool)]
        public bool Enable;
        public IntPtr BlurRegion;
        [MarshalAs(UnmanagedType.Bool)]
        public bool TransitionOnMaximized;
    }

    private const uint DWM_BB_ENABLE = 0x00000001;
    private const uint DWM_BB_BLURREGION = 0x00000002;

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);

    [DllImport("dwmapi.dll")]
    private static extern int DwmEnableBlurBehindWindow(IntPtr hWnd, ref DwmBlurBehind blurBehind);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRectRgn(int x1, int y1, int x2, int y2);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern int FillRect(IntPtr hDC, ref RECT lprc, IntPtr hbr);

    [DllImport("gdi32.dll")]
    private static extern IntPtr GetStockObject(int i);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    /// <summary>DIP → 物理像素（Win32 区域/几何一律用物理像素）。</summary>
    private static int DipsToPixels(IntPtr hwnd, int dips)
    {
        var dpi = hwnd != IntPtr.Zero ? GetDpiForWindow(hwnd) : 0;
        return dpi == 0 ? dips : (int)Math.Round(dips * dpi / 96.0);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();

    private const int BLACK_BRUSH = 4;

    // ---- Win11 的系统描边与系统圆角 ----
    //
    // Win11 上即使标题栏边框都去掉了，系统仍会给窗口加一圈描边和系统圆角。
    // 这两个属性是 21H2+ 才有的，Win10 上调用失败也不影响别的。

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_BORDER_COLOR = 34;
    private const uint DWMWCP_DONOTROUND = 1;
    private const uint DWMWCP_ROUND = 2;
    private const uint DWMWA_COLOR_NONE = 0xFFFFFFFE;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>
    /// 系统圆角 / 系统描边。
    ///
    /// - **Win11**：让 DWM 自己画圆角（DWMWCP_ROUND）。它带抗锯齿，
    ///   阴影也是对的，比自绘裁剪强得多。
    /// - **Win10**：没有这个属性，调用会失败，但不影响别的（圆角走自绘）。
    ///
    /// 两种情况下都把系统描边去掉——否则透明窗口外面会有一圈系统灰边。
    /// </summary>
    private static void ApplySystemBorder(IntPtr hwnd, bool useSystemCorner)
    {
        var corner = (int)(useSystemCorner ? DWMWCP_ROUND : DWMWCP_DONOTROUND);
        var border = unchecked((int)DWMWA_COLOR_NONE);

        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
        DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref border, sizeof(int));
    }

    /// <summary>
    /// 把 DWM 的透明配置施加到窗口上。
    ///
    /// <c>DwmExtendFrameIntoClientArea</c> 这里用 0（不扩展）：扩展帧由
    /// WinUIEx 的 TransparentTintBackdrop 负责；这里真正起作用的是后面的
    /// <c>DwmEnableBlurBehindWindow</c> + 空区域——让 DWM 不再铺窗口底色，
    /// XAML 没画的地方（圆角外）才能透出桌面。
    ///
    /// DWM 合成状态一变（焦点切换就会触发），blur behind 配置会被重置，
    /// 窗口随即退回不透明白底——所以每次收到 WM_DWMCOMPOSITIONCHANGED 都要重做。
    /// </summary>
    private static void ApplyDwmTransparency(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var margins = new Margins { Left = 0, Top = 0, Right = 0, Bottom = 0 };
        DwmExtendFrameIntoClientArea(hwnd, ref margins);

        var empty = CreateRectRgn(-2, -2, -1, -1);
        if (empty == IntPtr.Zero)
        {
            return;
        }

        try
        {
            var data = new DwmBlurBehind
            {
                Flags = DWM_BB_ENABLE | DWM_BB_BLURREGION,
                Enable = true,
                BlurRegion = empty,
                TransitionOnMaximized = false,
            };

            DwmEnableBlurBehindWindow(hwnd, ref data);
        }
        finally
        {
            DeleteObject(empty);
        }
    }

    /// <summary>
    /// 把窗口最外圈裁掉 <see cref="OuterTrim"/> 像素。
    ///
    /// 现象：窗口最外缘会挂一条约 8px 的半透明灰边，疑似 DWM 合成缓存没及时更新，
    /// 残留了旧表面的内容。既然脏边就在最外圈，把这一圈从**窗口区域**里去掉，
    /// 它就没地方可显示。
    ///
    /// 这里裁的是**矩形**（不是圆角），边缘是硬的，但裁出来的边落在透明区里，
    /// 所以看不出硬边。
    ///
    /// 注意：只在**顶层真实窗口**上做。嵌入的子窗口是 WS_CHILD，没有 DWM 那一套，
    /// 也没有这条脏边，保持原样。
    /// </summary>
    private static void ApplyOuterTrim(IntPtr hwnd) =>
        ApplyOuterTrim(hwnd, -1, -1);

    /// <summary>
    /// 用**指定尺寸**裁剪窗口最外圈。
    ///
    /// 尺寸必须取自 <c>WM_SIZE</c> 的 lParam，不能用 GetWindowRect：
    /// WM_SIZE 处理期间窗口尺寸还没更新，GetWindowRect 拿到的还是**旧尺寸**，
    /// 区域就按旧尺寸裁好了——窗口随后变大，区域却还是小的，
    /// 表现就是「初始化时窗口缩成一团，最小化再还原就好了」。
    /// </summary>
    private static void ApplyOuterTrim(IntPtr hwnd, int width, int height)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        if (width < 0 || height < 0)
        {
            if (!GetWindowRect(hwnd, out var bounds))
            {
                return;
            }

            width = bounds.Right - bounds.Left;
            height = bounds.Bottom - bounds.Top;
        }

        // Win11 上圆角由 DWM 画，它需要**完整的矩形窗口**——
        // 再用 SetWindowRgn 裁一刀，系统圆角就被切没了。
        if (IsWindows11)
        {
            SetWindowRgn(hwnd, IntPtr.Zero, false);
            return;
        }

        // 最大化时窗口就是整块屏幕，最外圈不该再裁——
        // 裁掉会露出屏幕边缘的一条缝。
        const long WS_MAXIMIZE = 0x01000000L;
        if ((GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64() & WS_MAXIMIZE) != 0)
        {
            SetWindowRgn(hwnd, IntPtr.Zero, false);
            return;
        }

        // WindowChrome.OuterTrim 是 DIP（XAML 的 Padding 按 DIP 布局），
        // 而 SetWindowRgn / WM_SIZE 的宽高都是物理像素。高 DPI 下必须换算，
        // 否则裁的边比 XAML 留的透明边宽（或窄），边框线就错位。
        var trim = DipsToPixels(hwnd, WindowChrome.OuterTrim);
        if (width <= trim * 2 || height <= trim * 2)
        {
            // 尺寸还没就绪：把之前裁的区域清掉，别留个小的把窗口卡住
            SetWindowRgn(hwnd, IntPtr.Zero, false);
            return;
        }

        // 先把旧区域清掉，否则设不进去
        SetWindowRgn(hwnd, IntPtr.Zero, false);

        var region = CreateRectRgn(trim, trim, width - trim, height - trim);

        if (region == IntPtr.Zero)
        {
            return;
        }

        // 成功后区域归系统所有，不能再 DeleteObject
        if (SetWindowRgn(hwnd, region, true) == 0)
        {
            DeleteObject(region);
        }
    }

    /// <summary>窗口最外圈要裁掉的 DIP；用之前经 DipsToPixels 换成物理像素。</summary>
    private const int OuterTrim = WindowChrome.OuterTrim;

    /// <summary>
    /// 清掉窗口样式里与系统边框相关的位。
    ///
    /// 保留 WS_THICKFRAME 的话窗口外轮廓仍是一圈看不见的方框。
    /// </summary>
    private static void StripBorderStyles(IntPtr hwnd)
    {
        var style = GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64();
        var stripped = style & ~(WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX
                                 | WS_MAXIMIZEBOX | WS_THICKFRAME);

        if (stripped == style)
        {
            return;
        }

        SetWindowLongPtr(hwnd, GWL_STYLE, new IntPtr(stripped));
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE
            | SWP_FRAMECHANGED | SWP_SHOWWINDOW);

        HostLog.Write($"[chrome] 系统标题栏已移除 style 0x{style:X} -> 0x{stripped:X}");
    }

    // ---- 客户区归零 ----
    //
    // WS_THICKFRAME 会带来一圈 7~8px 的非客户区，客户区因此比窗口小一圈。
    // 自绘圆角落在客户区内容上时，圆角会缩进一圈，窗口外轮廓还是方的；
    // 窗口透明之后那圈方角会直接露出来，看起来就是「根本没做圆角」。
    //
    // 在 WM_NCCALCSIZE（wParam=TRUE）里返回 0，等于告诉系统「客户区 = 整个窗口」。

    private delegate IntPtr SubclassProc(
        IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam,
        UIntPtr idSubclass, IntPtr refData);

    private const uint WM_NCCALCSIZE = 0x0083;
    private const uint WM_ERASEBKGND = 0x0014;
    private const uint WM_DWMCOMPOSITIONCHANGED = 0x031E;
    private const uint WM_NCPAINT = 0x0082;
    private const uint WM_SIZE = 0x0005;
    private const uint WM_GETMINMAXINFO = 0x0024;

    private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    // comctl32 的序号导出。注意 412 / 413 容易写反：
    //   410 = SetWindowSubclass, 412 = RemoveWindowSubclass, 413 = DefSubclassProc
    [DllImport("comctl32.dll", EntryPoint = "#410")]
    private static extern bool SetWindowSubclass(
        IntPtr hWnd, SubclassProc proc, UIntPtr id, UIntPtr data);

    [DllImport("comctl32.dll", EntryPoint = "#413")]
    private static extern IntPtr DefSubclassProc(
        IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    /// <summary>必须存成静态字段：委托被 GC 回收后，窗口过程会跳到已释放的地址。</summary>
    private static readonly SubclassProc NonClientProc = OnNonClientMessage;

    private static readonly HashSet<IntPtr> HookedWindows = new();

    private const uint WM_NCHITTEST = 0x0084;
    private const int HTTRANSPARENT = -1;

    [DllImport("user32.dll")]
    private static extern bool ScreenToClient(IntPtr hWnd, ref POINT point);

    /// <summary>
    /// 四角透明区让鼠标穿透：XAML 圆角裁剪只是不画像素，角上的矩形区域
    /// 仍属于 HWND，不处理的话会挡住后面窗口的点击（尤其子窗口叠在一起时）。
    /// </summary>
    private static bool InRoundedCorner(IntPtr hwnd, int x, int y, int width, int height)
    {
        // 和渲染层 WindowCornerRadius 一致；最大化/Win11 系统圆角时不裁
        if (IsWindows11 || (GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64() & 0x01000000L) != 0)
        {
            return false;
        }

        var radius = DipsToPixels(hwnd, 8);
        if (radius <= 0)
        {
            return false;
        }

        // 只检查四个角的小方框
        var cx = x < radius ? 0 : (x >= width - radius ? width : -1);
        var cy = y < radius ? 0 : (y >= height - radius ? height : -1);
        if (cx < 0 || cy < 0)
        {
            return false;
        }

        var dx = x - cx;
        var dy = y - cy;
        // 圆外（到角点的距离大于半径）才算透明区
        return dx * dx + dy * dy > radius * radius;
    }

    private static IntPtr OnNonClientMessage(
        IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam,
        UIntPtr idSubclass, IntPtr refData)
    {
        if (msg == WM_NCCALCSIZE && wParam != IntPtr.Zero)
        {
            // 客户区覆盖整个窗口，非客户区为 0
            return IntPtr.Zero;
        }

        if (msg == WM_NCHITTEST)
        {
            // lParam 是屏幕坐标，换成客户区坐标后判四角。
            // 命中透明角就返回 HTTRANSPARENT 让消息落到下层窗口。
            var pt = new POINT
            {
                X = unchecked((int)(long)lParam & 0xFFFF),
                Y = unchecked(((int)(long)lParam >> 16) & 0xFFFF),
            };
            ScreenToClient(hWnd, ref pt);
            if (GetClientRect(hWnd, out var client)
                && InRoundedCorner(hWnd, pt.X, pt.Y,
                    client.Right - client.Left, client.Bottom - client.Top))
            {
                return new IntPtr(HTTRANSPARENT);
            }

            return DefSubclassProc(hWnd, msg, wParam, lParam);  // 其余区域交给默认/后续子类
        }

        if (msg == WM_GETMINMAXINFO)
        {
            // 最大化只占**工作区**，不压任务栏。
            //
            // 默认行为是最大化到整块屏幕（含任务栏那条），
            // 我们把最大位置/尺寸改成 rcWork（显示器工作区），
            // 客户区归零之后，窗口内容正好铺满工作区、停在任务栏上沿。
            var monitor = MonitorFromWindow(hWnd, MONITOR_DEFAULTTONEAREST);
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };

            if (GetMonitorInfo(monitor, ref info))
            {
                var mi = Marshal.PtrToStructure<MINMAXINFO>(lParam);

                mi.ptMaxPosition = new POINT
                {
                    X = info.rcWork.Left - info.rcMonitor.Left,
                    Y = info.rcWork.Top - info.rcMonitor.Top,
                };
                mi.ptMaxSize = new POINT
                {
                    X = info.rcWork.Right - info.rcWork.Left,
                    Y = info.rcWork.Bottom - info.rcWork.Top,
                };

                Marshal.StructureToPtr(mi, lParam, true);
            }

            return IntPtr.Zero;
        }

        if (msg == WM_SIZE)
        {
            // 最大化/还原、改尺寸都要重算最外圈的裁剪区域。
            // 尺寸取 lParam 里的**新值**——此刻 GetWindowRect 还是旧的。
            var size = lParam.ToInt64();
            var newWidth = (int)(size & 0xFFFF);
            var newHeight = (int)((size >> 16) & 0xFFFF);

            ApplyOuterTrim(hWnd, newWidth, newHeight);

            // 处理完继续往下传：WinUIEx 的 backdrop 等也挂了子类，
            // 直接 return 会吞掉消息，让那些子类的状态跟不上。
            return DefSubclassProc(hWnd, msg, wParam, lParam);
        }

        if (msg == WM_DWMCOMPOSITIONCHANGED)
        {
            // DWM 合成状态一变（焦点切换就会触发），blur behind 的配置会被重置，
            // 窗口随即退回不透明的白底。这里立刻重做一遍，然后照样往下传。
            ApplyDwmTransparency(hWnd);
            return DefSubclassProc(hWnd, msg, wParam, lParam);
        }

        // WM_ERASEBKGND 不拦：由 WinUIEx 的 TransparentTintBackdrop 处理，
        // 它会用黑刷把客户区清空。

        return DefSubclassProc(hWnd, msg, wParam, lParam);
    }

    private static void HookNonClientCalc(IntPtr hwnd)
    {
        if (!HookedWindows.Add(hwnd))
        {
            return;
        }

        if (!SetWindowSubclass(hwnd, NonClientProc, 2, UIntPtr.Zero))
        {
            HostLog.Write("[chrome] SetWindowSubclass 失败，非客户区无法归零");
        }
    }

    /// <summary>
    /// 嵌入用的子窗口（WS_CHILD）也做圆角处理。
    ///
    /// 子窗口和顶层窗口不一样：
    /// - 顶层窗口靠 DWM 的扩展帧 + blur behind 让**客户区**透出桌面；
    /// - 子窗口是 WS_CHILD，DWM 那套对子窗口无效（只支持顶层窗口），
    ///   它画出来的是窗口类登记的背景刷——一块不透明的方块，圆角外也是它。
    ///
    /// 所以子窗口走另一条路：客户区归零（内容铺满）+ **不擦背景**
    /// （WM_ERASEBKGND 直接返回，不填任何颜色）。圆角外的像素就保留父窗口
    /// 已经画好的内容，配合 XAML 侧的圆角裁剪，子窗口看上去就是融进父窗口的。
    /// </summary>
    public static void EnableEmbedded(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !EmbeddedWindows.Add(hwnd))
        {
            return;
        }

        if (!SetWindowSubclass(hwnd, EmbeddedProc, 3, UIntPtr.Zero))
        {
            HostLog.Write("[chrome] 子窗口 SetWindowSubclass 失败");
        }
    }

    private static readonly HashSet<IntPtr> EmbeddedWindows = new();

    private static readonly SubclassProc EmbeddedProc = OnEmbeddedMessage;

    private static IntPtr OnEmbeddedMessage(
        IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam,
        UIntPtr idSubclass, IntPtr refData)
    {
        if (msg == WM_NCCALCSIZE && wParam != IntPtr.Zero)
        {
            // 客户区覆盖整个子窗口，圆角才能作用到边缘
            return IntPtr.Zero;
        }

        if (msg == WM_ERASEBKGND)
        {
            // 什么都不画：这块保留父窗口已经画好的像素
            return new IntPtr(1);
        }

        return DefSubclassProc(hWnd, msg, wParam, lParam);
    }
}
