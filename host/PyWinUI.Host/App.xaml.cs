using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.UI;

namespace PyWinUI.Host;

/// <summary>
/// 应用对象。<c>partial</c> 是必须的——XAML 编译器会生成另一半，
/// 里面有 <c>InitializeComponent</c> 和类型元数据提供器。
/// </summary>
public sealed partial class HostApp : Application
{
    /// <summary>加载圈边长（DIP）。初始窗口按它的 3 倍见方来定。</summary>
    private const double RingSize = 56;

    /// <summary>
    /// 加载态至少显示这么久。
    ///
    /// 宿主初始化本身就要几百毫秒，Python 的 render 往往在窗口刚出现时就已经
    /// 在管道里排队了。不设下限的话加载圈只闪一两帧，看起来像界面抖了一下。
    /// 延迟的是「开始读 stdin」而不是「开始渲染」——管道有缓冲，消息不会丢。
    /// </summary>
    private const int LoadingFloorMs = 500;

    /// <summary>
    /// 静默模式下等首帧的上限。超时仍没收到 render 就弹失败提示，
    /// 不能让窗口一直不出现——那样用户只会看到「点了没反应」。
    /// </summary>
    private const int SilentTimeoutMs = 8000;

    /// <summary>静默初始尺寸（DIP）。首帧后会被协议里的 width/height 覆盖。</summary>
    private const int SilentWidth = 880;
    private const int SilentHeight = 600;

    private Window? _window;
    private Session? _session;
    private DispatcherQueueTimer? _silentTimer;
    private bool _silent;
    private bool _windowShown;

    public HostApp()
    {
        InitializeComponent();
        HostLog.Write("ctor: InitializeComponent done");

        UnhandledException += (_, e) => HostLog.Write("UnhandledException: " + e.Exception);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // 静默弹窗模式：--silent（Python 侧 App(popup=True) 会带上）。
        // 不显示加载动画、窗口先不亮，等首帧界面准备好后一次跳出。
        _silent = Array.Exists(
            Environment.GetCommandLineArgs(),
            a => a.Equals("--silent", StringComparison.OrdinalIgnoreCase));

        HostLog.Write($"OnLaunched: begin (silent={_silent})");

        _window = new Window
        {
            Title = _silent ? "应用" : "正在加载",
        };

        if (_silent)
        {
            // 只把尺寸/位置摆好，不设加载内容。
            // 仍然 Activate 一次：不激活的话 XamlRoot 为 null，
            // 部分控件（WebView2 等）在首帧渲染时会失败。
            // 窗口先被放到屏幕外，用户看不到任何中间状态。
            ApplyInitialGeometry(silent: true);
            _window.Activate();
            HostLog.Write("OnLaunched: silent, window parked off-screen");
        }
        else
        {
            // 先放一个加载占位：单独双击 exe 时也能确认宿主活着。
            // 收到 render 消息后，整个内容会被 Python 描述的控件树替换掉。
            _window.Content = BuildLoadingView();
            ApplyInitialGeometry(silent: false);
            _window.Activate();
            _windowShown = true;
            HostLog.Write("OnLaunched: window activated");
        }

        var dispatcher = DispatcherQueue.GetForCurrentThread();
        _session = new Session(_window, dispatcher);
        _session.FirstRenderApplied += OnFirstRenderApplied;
        _session.StartupFailed += OnStartupFailed;

        if (_silent)
        {
            // 静默模式跳过加载圈下限，立刻开始读协议
            _session.Start();
            StartSilentWatchdog(dispatcher);
            HostLog.Write("OnLaunched: silent, session started immediately");
        }
        else
        {
            StartSessionAfterFloor(dispatcher);
            HostLog.Write("OnLaunched: waiting for loading floor");
        }
    }

    /// <summary>首帧界面已应用：静默模式下这时才把窗口亮出来。</summary>
    private void OnFirstRenderApplied()
    {
        _silentTimer?.Stop();

        if (!_silent || _windowShown)
        {
            return;
        }

        _windowShown = true;
        CenterWindow();
        _window!.Activate();
        HostLog.Write("[silent] first frame applied, window shown");
    }

    /// <summary>
    /// 静默模式看门狗：迟迟等不到首帧（或 Python 侧提前断开）就把失败原因显示出来。
    /// 静默不等于「什么都不说」——失败必须看得见。
    /// </summary>
    private void StartSilentWatchdog(DispatcherQueue dispatcher)
    {
        _silentTimer = dispatcher.CreateTimer();
        _silentTimer.Interval = TimeSpan.FromMilliseconds(SilentTimeoutMs);
        _silentTimer.IsRepeating = false;
        _silentTimer.Tick += (_, _) =>
        {
            if (_windowShown)
            {
                return;
            }

            HostLog.Write("[silent] watchdog timeout, showing failure view");
            ShowStartupFailure("宿主在预期时间内没有收到界面描述。");
        };
        _silentTimer.Start();
    }

    /// <summary>启动阶段就失败了（如 Python 侧退出）：显示失败界面而不是静默关窗。</summary>
    private void OnStartupFailed(string? error)
    {
        _silentTimer?.Stop();
        HostLog.Write("[startup] failed: " + (error ?? "(no detail)"));
        ShowStartupFailure(error);
    }

    private void ShowStartupFailure(string? detail)
    {
        if (_window is null || _windowShown)
        {
            return;
        }

        _windowShown = true;
        _window.Title = "启动失败";
        _window.Content = BuildErrorView(
            "加载失败",
            string.IsNullOrWhiteSpace(detail)
                ? "宿主与 Python 侧的连接在界面渲染前就断开了。"
                : detail!);
        CenterWindow();
        _window.Activate();
    }

    /// <summary>
    /// 等加载圈显示满 <see cref="LoadingFloorMs"/> 再开始读协议。
    /// 用 DispatcherQueueTimer 而不是 Thread.Sleep：睡眠会冻住 UI 线程，
    /// 加载圈自己也不转了。
    /// </summary>
    private void StartSessionAfterFloor(DispatcherQueue dispatcher)
    {
        var timer = dispatcher.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(LoadingFloorMs);
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            HostLog.Write($"[loader] floor {LoadingFloorMs}ms elapsed, session starting");
            _session?.Start();
        };
        timer.Start();
    }

    /// <summary>
    /// 初始窗口：3 个加载环见方，居中到主显示器。
    ///
    /// 这一步只在加载阶段有效——Python 发来 render 后，窗口尺寸会被
    /// 协议里的 window.width / window.height 覆盖。
    /// </summary>
    private void ApplyInitialGeometry(bool silent)
    {
        if (_window is null)
        {
            return;
        }

        try
        {
            var appWindow = _window.AppWindow;
            var scale = GetDpiScale();

            // 静默模式给一个正常应用尺寸（首帧后会被协议覆盖），
            // 非静默模式给加载圈 3 倍见方的小方块。
            var width = silent ? (int)Math.Round(SilentWidth * scale) : (int)Math.Round(RingSize * 3 * scale);
            var height = silent ? (int)Math.Round(SilentHeight * scale) : width;

            appWindow.Resize(new SizeInt32(width, height));

            // 加载阶段这个窗口只是个提示，不该让人最大化或最小化。
            // 收尾能力由 Session 在收到第一条 render 时恢复。
            if (!silent && appWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.IsMinimizable = false;
                presenter.IsMaximizable = false;
                presenter.IsResizable = false;
            }

            if (silent)
            {
                // 静默模式：先停到屏幕外，首帧后再移回中央。
                // 用「屏幕外」而不是 Hide()：Hide 后 XamlRoot 可能失效，
                // 而且 Activate/Hide 之间会有可见闪烁。
                appWindow.Move(new PointInt32(-32000, -32000));
            }
            else
            {
                MoveToCenter();
            }

            HostLog.Write($"[window] initial geometry {width}x{height}px (scale={scale}, silent={silent}) centered");
        }
        catch (Exception ex)
        {
            HostLog.Write("initial geometry failed: " + ex.Message);
        }
    }

    /// <summary>把窗口居中到主显示器工作区。</summary>
    private void CenterWindow() => MoveToCenter();

    private void MoveToCenter()
    {
        if (_window is null)
        {
            return;
        }

        try
        {
            var appWindow = _window.AppWindow;
            var display = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary);
            var work = display.WorkArea;
            var size = appWindow.Size;

            appWindow.Move(new PointInt32(
                work.X + (work.Width - size.Width) / 2,
                work.Y + (work.Height - size.Height) / 2));
        }
        catch (Exception ex)
        {
            HostLog.Write("center window failed: " + ex.Message);
        }
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    private double GetDpiScale()
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window!);
            var dpi = GetDpiForWindow(hwnd);
            return dpi == 0 ? 1.0 : dpi / 96.0;
        }
        catch
        {
            return 1.0;
        }
    }

    /// <summary>
    /// 等待 Python 发来 render 之前的占位界面：居中转圈的加载指示器 + 一行说明。
    /// </summary>
    private static FrameworkElement BuildLoadingView()
    {
        var ring = new ProgressRing
        {
            IsActive = true,
            Width = RingSize,
            Height = RingSize,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        var caption = new TextBlock
        {
            Text = "正在加载环境...",
            FontSize = 14,
            HorizontalAlignment = HorizontalAlignment.Center,
            Opacity = 0.72,
        };

        var stack = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 18,
        };
        stack.Children.Add(ring);
        stack.Children.Add(caption);

        return stack;
    }

    /// <summary>
    /// 启动失败界面：明确告诉用户没起来，并给出原因。
    /// 静默模式尤其需要它——否则失败就是「窗口不出现」，用户完全不知道发生了什么。
    /// </summary>
    private static FrameworkElement BuildErrorView(string title, string detail)
    {
        var icon = new FontIcon
        {
            Glyph = "\uE783", // ErrorBadge
            FontSize = 40,
            Foreground = new SolidColorBrush(Color.FromArgb(255, 196, 43, 28)),
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        var titleBlock = new TextBlock
        {
            Text = title,
            FontSize = 20,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        var detailBlock = new TextBlock
        {
            Text = detail,
            FontSize = 13,
            LineHeight = 20,
            MaxWidth = 460,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Opacity = 0.72,
        };

        var stack = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 14,
            Padding = new Thickness(32),
        };
        stack.Children.Add(icon);
        stack.Children.Add(titleBlock);
        stack.Children.Add(detailBlock);

        return stack;
    }
}
