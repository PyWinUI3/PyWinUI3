using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using WinRT.Interop;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Media.Playback;
using PyWinUI.Host.Protocol;
using PyWinUI.Host.Rendering;
using PyWinUI.Host.Transport;

namespace PyWinUI.Host;

/// <summary>
/// 会话协调者：串起传输层、协议解析和渲染器。
///
/// 线程模型是这个类最需要注意的地方：
///
/// - 读 stdin 会阻塞，绝不能占 UI 线程，所以放在后台线程
/// - 所有 XAML 操作必须在 UI 线程，所以消息解析完要 DispatcherQueue 排队
/// - 写 stdout 是线程安全的（StdioChannel 内部加锁），UI 线程直接写
///
/// 这套分工和 WinUI 自身的设计一致，也是它不会像进程内托管那样死锁的原因。
/// </summary>
/// <summary>
/// 一扇窗口的完整上下文：窗口本身 + 绑定它的渲染器/查询器/micro 运行时 + 修订号。
///
/// 之所以按窗口整体打包，是因为 FormRenderer 的 emit 闭包必须知道事件来自哪扇窗，
/// 否则多窗口时所有事件都会带上同一个 session 标记，Python 侧无法路由。
/// </summary>
internal sealed class WindowContext
{
    public required string SessionId { get; init; }
    public required Window Window { get; init; }
    public FormRenderer Renderer { get; set; } = null!;
    public QueryExecutor Queries { get; set; } = null!;
    public MicroRuntime Micro { get; set; } = null!;
    public long Rev;
    public bool FirstRenderNotified;
    public bool Closed;
}

internal sealed class Session
{
    private readonly Window _window;
    private readonly DispatcherQueue _dispatcher;
    private readonly StdioChannel _channel;

    /// <summary>main 上下文就是宿主启动时 App.xaml.cs 创建的窗口。</summary>
    private readonly WindowContext _main;

    /// <summary>所有会话窗口，包括 main。新 render 带未知 session 时在这里建窗。</summary>
    private readonly Dictionary<string, WindowContext> _contexts = new();

    // main 的快捷访问：绝大多数单窗口代码路径不用改动
    private FormRenderer _renderer => _main.Renderer;
    private QueryExecutor _queries => _main.Queries;
    private MicroRuntime _micro => _main.Micro;
    private long _rev { get => _main.Rev; set => _main.Rev = value; }
    private bool _firstRenderNotified
    {
        get => _main.FirstRenderNotified;
        set => _main.FirstRenderNotified = value;
    }

    private bool _handshaken;
    private bool _closed;

    /// <summary>
    /// 首帧 render 已应用到窗口后触发一次。
    /// 静默启动（--silent）靠它决定「什么时候把窗口亮出来」——
    /// 在这之前窗口是隐藏的，避免用户看到加载圈或半成品界面。
    /// </summary>
    public event Action? FirstRenderApplied;

    /// <summary>
    /// 首帧之前就失败（Python 侧退出、管道断开等）。参数是错误描述，可能为 null。
    /// 订阅方负责把失败原因显示出来——静默模式下窗口还没亮，不提示就等于无响应。
    /// </summary>
    public event Action<string?>? StartupFailed;


    public Session(Window window, DispatcherQueue dispatcher)
    {
        _window = window;
        _dispatcher = dispatcher;
        _channel = new StdioChannel();
        _main = CreateContext("main", window);
        _contexts["main"] = _main;

        _window.Closed += OnWindowClosed;

        // 托盘回调走 Win32 消息，复用主窗口句柄挂 WndProc 子类
        TraySupport.Initialize(_window, (id, what) => Send(Outbound.TrayEvent(id, what)));
    }

    /// <summary>
    /// 为一扇新窗口建完整上下文。emit 闭包带上 sessionId 是关键：
    /// 事件回传时 Python 靠它路由到对应的 Window 实例。
    /// </summary>
    private WindowContext CreateContext(string sessionId, Window window)
    {
        var context = new WindowContext
        {
            SessionId = sessionId,
            Window = window,
        };

        context.Queries = new QueryExecutor(window);
        context.Renderer = new FormRenderer(
            window,
            (nodeId, eventName, data, echo) => Emit(sessionId, nodeId, eventName, data, echo),
            message => Warn(sessionId, message),
            (code, message, nodeId) => Send(Outbound.Error(code, message, nodeId, sessionId)));
        context.Micro = new MicroRuntime(
            context.Renderer,
            (nodeId, eventName, data, echo) => Emit(sessionId, nodeId, eventName, data, echo),
            line => Send(line));

        return context;
    }

    /// <summary>
    /// 为一扇新会话创建窗口。render 消息自带 window spec，
    /// 这里的尺寸只是首帧前的占位，收到 render 后会被覆盖。
    /// </summary>
    private WindowContext CreateWindow(string sessionId)
    {
        var window = new Window
        {
            Title = "PyWinUI3",
        };

        // XamlRoot 在 Activate 之前为 null，部分控件首帧会失败
        window.AppWindow.Resize(new Windows.Graphics.SizeInt32(800, 600));
        window.Activate();

        var context = CreateContext(sessionId, window);
        window.Closed += (_, _) => OnSessionWindowClosed(sessionId);
        _contexts[sessionId] = context;

        HostLog.Write($"new window for session {sessionId}");
        return context;
    }

    private void HandleTray(TrayMessage message)
    {
        try
        {
            switch (message.Action)
            {
                case "create":
                    TraySupport.Create(message.Id, message.Spec);
                    break;
                case "update":
                    TraySupport.Update(message.Id, message.Spec);
                    break;
                case "destroy":
                    TraySupport.Destroy(message.Id);
                    break;
                default:
                    Send(Outbound.Error("bad-protocol", $"未知的托盘动作: {message.Action}", null));
                    break;
            }
        }
        catch (Exception ex)
        {
            Send(Outbound.Error("internal", $"托盘操作失败: {ex.Message}", null));
        }
    }

    /// <summary>
    /// 窗口级控制：hide / show / close / focus。
    /// main 窗口关闭等于退出整个应用（保持原有语义）；
    /// 其他会话只关自己，宿主继续跑。
    /// </summary>
    private void HandleWindowAction(WindowActionMessage action)
    {
        var context = Context(action.Session);
        if (context is null)
        {
            return;
        }

        try
        {
            switch (action.Action)
            {
                case "hide":
                    ShowWindowNative(context.Window, 0);  // SW_HIDE
                    break;

                case "show":
                    ShowWindowNative(context.Window, 5);  // SW_SHOW
                    context.Window.Activate();
                    break;

                case "focus":
                    context.Window.Activate();
                    break;

                case "close":
                    if (ReferenceEquals(context, _main))
                    {
                        Shutdown(0);
                    }
                    else
                    {
                        CloseSessionWindow(context, 0);
                    }

                    break;

                default:
                    Send(Outbound.Error(
                        "bad-protocol",
                        $"未知的 windowAction: {action.Action}",
                        null,
                        action.Session));
                    break;
            }
        }
        catch (Exception ex)
        {
            Send(Outbound.Error("internal", $"窗口命令 {action.Action} 失败: {ex.Message}", null, action.Session));
        }
    }

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private static void ShowWindowNative(Window window, int command)
    {
        var hwnd = WindowNative.GetWindowHandle(window);
        ShowWindow(hwnd, command);
    }

    /// <summary>关闭一个非 main 会话窗口：回通知、清状态，宿主继续。</summary>
    private void CloseSessionWindow(WindowContext context, int code)
    {
        if (context.Closed)
        {
            return;
        }

        context.Closed = true;
        _contexts.Remove(context.SessionId);

        try
        {
            context.Window.Close();
        }
        catch
        {
            // 已经关掉就忽略
        }

        Send(Outbound.WindowClosed(context.SessionId, code));
        HostLog.Write($"session {context.SessionId} window closed");
    }

    private void OnSessionWindowClosed(string sessionId)
    {
        if (_contexts.TryGetValue(sessionId, out var context) && !context.Closed)
        {
            context.Closed = true;
            _contexts.Remove(sessionId);
            Send(Outbound.WindowClosed(sessionId, 0));
        }
    }

    /// <summary>按会话 id 取窗口上下文；未知 id 回一条错误并返回 null。</summary>
    private WindowContext? Context(string sessionId, string? nodeId = null)
    {
        if (_contexts.TryGetValue(sessionId, out var context) && !context.Closed)
        {
            return context;
        }

        Send(Outbound.Error(
            "unknown-session",
            $"会话不存在或已关闭: {sessionId}",
            nodeId,
            sessionId));
        return null;
    }

    public void Start()
    {
        _channel.LineReceived += OnLineReceived;
        _channel.Closed += OnInputClosed;
        _channel.Start();
        HostLog.Write("session started, waiting for hello");
    }

    // ==================================================================
    // 入站
    // ==================================================================

    private void OnLineReceived(string line)
    {
        if (_closed)
        {
            return;
        }

        // 逐条报文只走 TRACE 级：默认不落盘，避免每条消息一次同步磁盘 IO
        HostLog.Trace("<< " + Truncate(line));

        InboundMessage message;
        try
        {
            message = Inbound.Parse(line);
        }
        catch (ProtocolException ex)
        {
            HostLog.Write($"[protocol] {ex.Code}: {ex.Message}");
            Send(Outbound.Error(ex.Code, ex.Message, ex.NodeId));

            if (ex.IsFatal)
            {
                _dispatcher.TryEnqueue(() => Shutdown(1));
            }

            return;
        }
        catch (Exception ex)
        {
            // Inbound 里不只有 ProtocolException：JsonNode.Parse、强制转换、
            // GetDouble 都可能抛别的异常。让它逃出读线程会被收尾成 Closed，
            // 宿主静默退出，Python 只看到 bye，日志里连原因都没有。
            HostLog.Write(ex, "[protocol] 解析异常");
            Send(Outbound.Error("bad-json", $"消息无法解析: {ex.Message}", null));
            return;
        }

        // 解析在后台线程完成，渲染必须回 UI 线程
        _dispatcher.TryEnqueue(() =>
        {
            try
            {
                Handle(message);
            }
            catch (Exception ex)
            {
                HostLog.Write(ex, "[session] 处理消息失败");
                Send(Outbound.Error("internal", $"处理 {message.GetType().Name} 失败: {ex.Message}", null));
            }
        });
    }

    private void Handle(InboundMessage message)
    {
        if (_closed)
        {
            return;
        }

        if (!_handshaken)
        {
            HandleHandshake(message);
            return;
        }

        switch (message)
        {
            case RenderMessage render:
            {
                // 新 session 的 render 就是「开一扇新窗口」；main 复用启动时那扇
                var context = _contexts.TryGetValue(render.Session, out var existing)
                    ? existing
                    : CreateWindow(render.Session);

                context.Rev = render.Rev;
                // 窗口能力（最大化/最小化）由 ApplyWindowSpec 在成功落地窗口属性时一并交还，
                // 渲染失败时窗口保持加载态的小尺寸——那里能最大化毫无意义。
                context.Renderer.Render(render);
                Send(Outbound.Ack(render.Rev));

                if (!context.FirstRenderNotified)
                {
                    context.FirstRenderNotified = true;
                    if (ReferenceEquals(context, _main))
                    {
                        FirstRenderApplied?.Invoke();
                    }
                }

                break;
            }

            case PatchMessage patch:
            {
                var context = Context(patch.Session);
                if (context is null)
                {
                    break;
                }

                context.Rev = patch.Rev;
                context.Renderer.Apply(patch);
                // 不再逐 patch 回 ack：Python 侧从来不消费它（App._dispatch 直接忽略），
                // 却要为每条付出一次 JSON 构造 + 管道 Flush + 对端 decode + 队列。
                // ack 现在只作为 render 的「首帧已应用」信号保留。
                break;
            }

            case InvokeMessage invoke:
                HandleInvoke(invoke);
                break;

            case QueryMessage query:
                HandleQuery(query);
                break;

            case MicroMessage micro:
            {
                var context = Context(micro.Session, micro.Node);
                if (context is not null)
                {
                    context.Micro.Run(micro);
                }
                break;
            }

            case WindowActionMessage windowAction:
                HandleWindowAction(windowAction);
                break;

            case TrayMessage tray:
                HandleTray(tray);
                break;

            case UnknownMessage unknown:
                // 协议第 7 章：新增 op 不算不兼容变更。旧端忽略即可，
                // 不能像以前那样把 bad-protocol 当致命错误直接掐断会话。
                Warn($"宿主不支持消息 op \"{unknown.Op}\"，已忽略（建议同步升级宿主）");
                break;

            case DialogMessage dialog:
                // 模态对话框关闭后 Python 侧不做任何同步等待，回 ack 没有意义；
                // 协议 2.2 也写明 dialog 不回 ack（它在 ShowDialog 里才结束时，
                // 这条 ack 已经晚到，纯属多一条出站流量）。
                ShowDialog(dialog);
                break;

            case FileDialogMessage fileDialog:
                // 与 dialog 不同，文件选择必须把路径带回来，所以有 fileDialogResult 应答。
                ShowFileDialog(fileDialog);
                break;

            case CloseMessage close:
                Shutdown(close.Code);
                break;

            case HelloMessage:
                Send(Outbound.Error("bad-protocol", "重复的 hello 消息", null));
                break;
        }
    }

    private void HandleHandshake(InboundMessage message)
    {
        if (message is not HelloMessage hello)
        {
            HostLog.Write("first message is not hello, aborting");
            Send(Outbound.Error("bad-protocol", "首条消息必须是 hello", null));
            Shutdown(1);
            return;
        }

        if (hello.Protocol != Wire.Version)
        {
            HostLog.Write($"protocol mismatch: client={hello.Protocol} host={Wire.Version}");
            Send(Outbound.Error(
                "version-mismatch",
                $"协议版本不匹配：客户端 {hello.Protocol}，宿主 {Wire.Version}",
                null));
            Shutdown(1);
            return;
        }

        _handshaken = true;
        HostLog.Write($"handshake ok (client={hello.Client} pid={hello.Pid})");

        Send(Outbound.Ready(Capabilities.DescribeOs(), Capabilities.Detect()));
    }

    private void HandleInvoke(InvokeMessage invoke)
    {
        var ctx = Context(invoke.Session, invoke.Id);
        if (ctx is null)
        {
            return;
        }

        // 窗口级命令不依赖任何节点，先处理，避免根节点还没建好时被挡住
        if (TryWindowInvoke(invoke.Method, ctx))
        {
            return;
        }

        if (!ctx.Renderer.TryGetElement(invoke.Id, out var element))
        {
            Send(Outbound.Error("unknown-node", $"invoke 的目标不存在: {invoke.Id}", invoke.Id));
            return;
        }

        try
        {
            switch (invoke.Method)
            {
                case "focus":
                    if (element is Control control)
                    {
                        control.Focus(FocusState.Programmatic);
                    }
                    else
                    {
                        element.Focus(FocusState.Programmatic);
                    }

                    break;

                case "select_all":
                    switch (element)
                    {
                        case TextBox box:
                            box.SelectAll();
                            break;
                        case PasswordBox passwordBox:
                            passwordBox.SelectAll();
                            break;
                        case RichEditBox richBox:
                            richBox.Document.Selection.Expand(Microsoft.UI.Text.TextRangeUnit.Story);
                            break;
                    }

                    break;

                case "select_tab":
                    HandleSelectTab(invoke, element);
                    break;

                case "scroll_to":
                    var index = ValueParsers.AsInt(invoke.Args?["index"]) ?? 0;
                    // ListViewBase 同时覆盖 ListView 和 GridView
                    if (element is ListViewBase list && list.Items.Count > index && index >= 0)
                    {
                        list.ScrollIntoView(list.Items[index]);
                    }

                    break;

                case "play":
                    if (element is MediaPlayerElement playMedia)
                    {
                        playMedia.MediaPlayer?.Play();
                    }

                    break;

                case "pause":
                    if (element is MediaPlayerElement pauseMedia)
                    {
                        pauseMedia.MediaPlayer?.Pause();
                    }

                    break;

                case "stop":
                    if (element is MediaPlayerElement stopMedia
                        && stopMedia.MediaPlayer is { } stopPlayer)
                    {
                        // MediaPlayer 没有 Stop()，只能暂停后把播放位置归零
                        stopPlayer.Pause();
                        stopPlayer.PlaybackSession.Position = TimeSpan.Zero;
                    }

                    break;

                case "seek":
                    if (element is MediaPlayerElement seekMedia
                        && seekMedia.MediaPlayer is { } seekPlayer)
                    {
                        var seconds = ValueParsers.AsDouble(invoke.Args?["seconds"]) ?? 0;
                        seekPlayer.PlaybackSession.Position =
                            TimeSpan.FromSeconds(Math.Max(0, seconds));
                    }

                    break;

                // WebView2 的导航动作。降级占位是 Border，匹配不上就静默跳过：
                // 运行时都没装，刷新/后退本来也没有意义。
                case "navigate":
                    if (element is WebView2 navigateWeb
                        && ValueParsers.AsString(invoke.Args?["url"]) is { } url
                        && Uri.TryCreate(url, UriKind.Absolute, out var target))
                    {
                        navigateWeb.Source = target;
                    }

                    break;

                case "navigate_to_string":
                    if (element is WebView2 htmlWeb
                        && ValueParsers.AsString(invoke.Args?["html"]) is { } html)
                    {
                        // 冷启动时 CoreWebView2 可能还没好，直接调会抛异常，见下方 helper
                        _ = NavigateToStringAsync(htmlWeb, html, invoke.Id);
                    }

                    break;

                case "reload":
                    if (element is WebView2 reloadWeb)
                    {
                        reloadWeb.Reload();
                    }

                    break;

                case "go_back":
                    if (element is WebView2 backWeb && backWeb.CanGoBack)
                    {
                        backWeb.GoBack();
                    }

                    break;

                case "go_forward":
                    if (element is WebView2 forwardWeb && forwardWeb.CanGoForward)
                    {
                        forwardWeb.GoForward();
                    }

                    break;

                default:
                    if (!ExtendedInvoke.TryInvoke(
                            element, invoke.Method, invoke.Args, invoke.Id,
                            message => HostLog.Write("[warn] " + message), Emit))
                    {
                        Send(Outbound.Error(
                            "bad-protocol",
                            $"未知的 invoke 方法: {invoke.Method}",
                            invoke.Id));
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            Send(Outbound.Error("internal", $"invoke {invoke.Method} 失败: {ex.Message}", invoke.Id));
        }
    }

    /// <summary>
    /// 选中标签页 / 固定选项卡。
    ///
    /// 两者都是 TabViewItem，直接把 TabView.SelectedItem 指过去；
    /// 父级从渲染器的节点表取，而不是爬可视树——未选中的标签页往往还没实体化，
    /// 爬树会找不到归属，表现为「select() 静默无效」。
    ///
    /// 用户绑了 tabSelected 时，SelectionChanged 会自己回事件，这里不再重复发；
    /// 没绑时补一条，保证「调 select() 就有一条 tabSelected」的约定成立。
    /// </summary>
    private void HandleSelectTab(InvokeMessage invoke, FrameworkElement element)
    {
        if (element is not TabViewItem tab
            || !Context(invoke.Session, invoke.Id)!.Renderer.TryGetOwner(invoke.Id, out var owner)
            || owner is not TabView tabs)
        {
            Send(Outbound.Error(
                "bad-protocol",
                $"select_tab 只能用于 TabPage / PinnedTab，收到 {element.GetType().Name}",
                invoke.Id));
            return;
        }

        var alreadyBound = Context(invoke.Session, invoke.Id)!.Renderer.IsEventBound(invoke.Id, "tabSelected");
        tabs.SelectedItem = tab;

        if (!alreadyBound)
        {
            Emit(
                invoke.Id,
                "tabSelected",
                new JsonObject { ["value"] = ExtendedControls.TabHeaderText(tab) },
                false);
        }
    }

    /// <summary>
    /// 最小化 / 最大化 / 还原 / 激活。这些是 OverlappedPresenter 的能力，
    /// 全屏或紧凑型 presenter 下静默跳过，不让整个窗口报错。
    /// </summary>
    private bool TryWindowInvoke(string method, WindowContext ctx)
    {
        if (method is not ("minimize" or "maximize" or "restore" or "activate"))
        {
            return false;
        }

        try
        {
            if (method == "activate")
            {
                ctx.Window.Activate();
                return true;
            }

            if (ctx.Window.AppWindow.Presenter is OverlappedPresenter presenter)
            {
                switch (method)
                {
                    case "minimize": presenter.Minimize(); break;
                    case "maximize": presenter.Maximize(); break;
                    case "restore": presenter.Restore(); break;
                }
            }
        }
        catch (Exception ex)
        {
            Send(Outbound.Error("internal", $"窗口命令 {method} 失败: {ex.Message}", null));
        }

        return true;
    }

    /// <summary>
    /// 读一个运行时值。query 走的是和 render/patch 相同的 UI 线程调度，
    /// 所以读取的是「已经落地的最新状态」，不会读到半应用完的补丁。
    /// 节点不存在时仍然允许窗口级键（例如 windowState），其余回 ok=false。
    /// </summary>
    private void HandleQuery(QueryMessage query)
    {
        var ctx = Context(query.Session, query.Node);
        if (ctx is null)
        {
            Send(Outbound.Result(query.Id, false, null, "会话不存在", query.Session));
            return;
        }

        ctx.Renderer.TryGetElement(query.Node, out var element);
        try
        {
            var value = ctx.Queries.Read(element, query.Key, query.Args);
            Send(Outbound.Result(query.Id, true, value, null, query.Session));
        }
        catch (Exception ex)
        {
            Send(Outbound.Result(query.Id, false, null, ex.Message, query.Session));
        }
    }

    /// <summary>
    /// NavigateToString 要求 CoreWebView2 已经就绪。控件刚建好、Runtime 冷启动时
    /// Core 还在异步初始化，此时直接调会抛「valid CoreWebView2 is not present」。
    /// HandleInvoke 本身跑在 UI 线程，await 会把控制权交回消息泵，不会死锁。
    /// </summary>
    private async Task NavigateToStringAsync(WebView2 web, string html, string? nodeId)
    {
        try
        {
            if (web.CoreWebView2 is null)
            {
                await web.EnsureCoreWebView2Async();
            }

            web.NavigateToString(html);
        }
        catch (Exception ex)
        {
            HostLog.Write("[webview2] NavigateToString failed: " + ex.Message);
            Send(Outbound.Error("internal", $"navigate_to_string 失败: {ex.Message}", nodeId));
        }
    }

    // ==================================================================
    // 窗口装饰
    // ==================================================================

    // 加载态 -> 常规态的窗口装饰切换已并入 FormRenderer.ApplyWindowSpec：
    // 那里能同时看到协议给的 resizable / minimizable / maximizable，
    // 分两处设置会互相覆盖。

    // ==================================================================
    // 对话框
    // ==================================================================

    /// <summary>正在显示对话框的会话集合：ContentDialog 同一 XamlRoot 只能开一个。</summary>
    private readonly HashSet<string> _dialogSessions = new();

    /// <summary>
    /// 显示模态对话框。
    ///
    /// WinUI 限制同一时刻只能有一个 ContentDialog，所以这里用标志位挡住并发请求——
    /// 不加这个判断，连续触发会抛「Only a single ContentDialog can be open at any time」。
    /// </summary>
    private async void ShowDialog(DialogMessage message)
    {
        var ctx = Context(message.Session);
        if (ctx is null)
        {
            return;
        }

        // 同一会话只能有一个 ContentDialog；不同会话各有 XamlRoot，可以并发
        if (!_dialogSessions.Add(message.Session))
        {
            Send(Outbound.Error("busy", "该窗口已有对话框未关闭，本次请求被丢弃", null, message.Session));
            return;
        }

        // 窗口 Content 刚切换完的那一帧 XamlRoot 可能还是 null，
        // 此时直接放弃会让「启动后立刻弹提示」这种常见用法莫名失效。
        // 等几帧再取，最多 500ms；ContentDialog 本来就是异步的，等得起。
        var xamlRoot = ctx.Window.Content?.XamlRoot;
        for (var attempt = 0; attempt < 10 && xamlRoot is null; attempt++)
        {
            await Task.Delay(50);
            xamlRoot = ctx.Window.Content?.XamlRoot;
        }

        if (xamlRoot is null)
        {
            _dialogSessions.Remove(message.Session);
            Send(Outbound.Error("internal", "窗口还没有内容，无法显示对话框", null, message.Session));
            return;
        }

        try
        {
            // kind 以前解析了却没用：info / warn / error 三个级别长得一模一样。
            // 这里用图标、颜色和按钮文字把级别显式区分开。
            var kind = (message.Kind ?? "info").ToLowerInvariant();
            var (glyph, accent, buttonText) = kind switch
            {
                "warn" or "warning" => ("\uE7BA", Microsoft.UI.Colors.Orange, "我知道了"),
                "error" => ("\uEA39", Microsoft.UI.Colors.OrangeRed, "关闭"),
                "success" => ("\uE930", Microsoft.UI.Colors.ForestGreen, "好的"),
                _ => ("\uE946", Microsoft.UI.Colors.DodgerBlue, "知道了"),
            };

            var body = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 12,
                Margin = new Thickness(0, 8, 0, 0),
            };
            body.Children.Add(new FontIcon
            {
                Glyph = glyph,
                FontSize = 26,
                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(accent),
                VerticalAlignment = VerticalAlignment.Center,
            });
            body.Children.Add(new TextBlock
            {
                Text = message.Body,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 380,
                VerticalAlignment = VerticalAlignment.Center,
            });

            var dialog = new ContentDialog
            {
                Title = string.IsNullOrEmpty(message.Title) ? "提示" : message.Title,
                Content = body,
                CloseButtonText = buttonText,
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = xamlRoot,
            };

            HostLog.Write($"[dialog] show ({kind}): {message.Title} / {message.Body}");
            await dialog.ShowAsync();
            HostLog.Write("[dialog] closed");
        }
        catch (Exception ex)
        {
            HostLog.Write("dialog failed: " + ex.Message);
            Send(Outbound.Error("internal", $"对话框显示失败: {ex.Message}", null));
        }
        finally
        {
            _dialogSessions.Remove(message.Session);
        }
    }

    /// <summary>
    /// 系统自带文件选择器。
    ///
    /// 系统 picker 是独立窗口，不占用 ContentDialog 的 _dialogOpen 名额，
    /// 但仍串行化——同时弹两个选择器在体验上也是事故。
    /// </summary>
    private async void ShowFileDialog(FileDialogMessage message)
    {
        var ctx = Context(message.Session);
        if (ctx is null)
        {
            Send(Outbound.FileDialogResult(message.Id, false, null, "会话不存在", message.Session));
            return;
        }

        // 文件选择器是独立窗口，按会话串行即可，不同窗口可并发
        if (!_dialogSessions.Add(message.Session))
        {
            Send(Outbound.FileDialogResult(message.Id, false, null, "该窗口已有对话框未关闭"));
            return;
        }

        try
        {
            var outcome = await SystemFilePicker.ShowAsync(
                ctx.Window,
                message.Mode,
                message.Title,
                message.InitialDir,
                message.Filter,
                message.DefaultName);
            Send(Outbound.FileDialogResult(message.Id, outcome.Ok, outcome.Paths,
                outcome.Ok ? null : "用户取消"));
        }
        catch (Exception ex)
        {
            HostLog.Write("file dialog failed: " + ex.Message);
            Send(Outbound.FileDialogResult(message.Id, false, null, $"文件对话框失败: {ex.Message}"));
        }
        finally
        {
            _dialogSessions.Remove(message.Session);
        }
    }

    /// <summary>等窗口 Content 的 XamlRoot 就绪，最多 500ms。</summary>
    private async Task<XamlRoot?> WaitForXamlRootAsync()
    {
        var xamlRoot = _window.Content?.XamlRoot;
        for (var attempt = 0; attempt < 10 && xamlRoot is null; attempt++)
        {
            await Task.Delay(50);
            xamlRoot = _window.Content?.XamlRoot;
        }

        return xamlRoot;
    }

    // ==================================================================
    // 出站
    // ==================================================================

    private void Emit(string nodeId, string eventName, JsonObject? data, bool echo) =>
        Emit("main", nodeId, eventName, data, echo);

    private void Emit(string sessionId, string nodeId, string eventName, JsonObject? data, bool echo)
    {
        if (_closed)
        {
            return;
        }

        if (!_contexts.TryGetValue(sessionId, out var context))
        {
            return;
        }

        Send(Outbound.Event(nodeId, eventName, context.Rev, data, echo, sessionId));
    }

    private void Send(string line)
    {
        HostLog.Trace(">> " + Truncate(line));
        _channel.Send(line);
    }

    // ==================================================================
    // 诊断回传
    // ==================================================================

    /// <summary>同一条告警在这个时间窗内只回传一次。</summary>
    private static readonly TimeSpan WarnDedupWindow = TimeSpan.FromSeconds(1);

    private readonly Dictionary<string, DateTime> _recentWarnings = new();
    private DateTime _lastWarnCleanup = DateTime.UtcNow;

    /// <summary>
    /// 渲染层的非致命告警。写日志之外再回传一条 <c>t="warning"</c>：
    /// 属性名拼错、控件类型写错、事件名写错这些如果只进 %TEMP% 文件，
    /// 使用者永远看不见，库就会变成一台「静默失败」的机器。
    ///
    /// 去重是必要的：渲染循环里同一条告警可能每帧产生一次，
    /// 不去重会把管道和 stderr 一起刷爆。窗口 1 秒。
    /// </summary>
    private void Warn(string message) => Warn("main", message);

    private void Warn(string sessionId, string message)
    {
        // 日志写在去重**之后**：旧实现放在最前面，渲染循环里同一条告警
        // 每帧产生一次时，回传被挡住了，磁盘写却一次没少。
        if (_closed)
        {
            return;
        }

        var now = DateTime.UtcNow;

        // 顺带清理过期条目，避免长会话里这个字典无限增长
        if ((now - _lastWarnCleanup).TotalSeconds > 10)
        {
            _lastWarnCleanup = now;
            var stale = _recentWarnings
                .Where(pair => (now - pair.Value) > TimeSpan.FromSeconds(5))
                .Select(pair => pair.Key)
                .ToList();
            foreach (var key in stale)
            {
                _recentWarnings.Remove(key);
            }
        }

        if (_recentWarnings.TryGetValue(message, out var last)
            && (now - last) < WarnDedupWindow)
        {
            return;
        }

        _recentWarnings[message] = now;
        HostLog.Write("[warn] " + message);
        Send(Outbound.Warning(message, null, sessionId));
    }

    private static string Truncate(string text) =>
        text.Length <= 500 ? text : text[..500] + $"…(+{text.Length - 500})";

    // ==================================================================
    // 关闭
    // ==================================================================

    private void OnInputClosed(string? error)
    {
        if (_closed)
        {
            return;
        }

        HostLog.Write(error is null
            ? "stdin closed (python side exited)"
            : $"stdin closed with error: {error}");

        // 首帧还没出来就断开：界面上什么都没有，直接关窗等于「静默失败」。
        // 交给上层把失败原因显示出来。
        if (!_firstRenderNotified && StartupFailed is not null)
        {
            _dispatcher.TryEnqueue(() => StartupFailed.Invoke(error));
            return;
        }

        // Python 那边没了，界面留着也没意义
        _dispatcher.TryEnqueue(() => Shutdown(0));
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        if (_closed)
        {
            return;
        }

        HostLog.Write("window closed by user");
        _closed = true;
        _channel.Send(Outbound.Bye(_exitCode));
        _channel.Dispose();
        Application.Current.Exit();
    }

    /// <summary>退出码。记在字段上，是因为关窗要经过 Closed 事件，那里拿不到局部变量。</summary>
    private int _exitCode;

    private void Shutdown(int code)
    {
        if (_closed)
        {
            return;
        }

        _exitCode = code;
        HostLog.Write($"shutdown (code={code})");

        // 清理托盘图标：Shell_NotifyIcon 加的图标不会随进程退出自动消失，
        // 不清会在通知区域留下僵尸图标，直到鼠标移上去才消失。
        foreach (var id in TraySupport.AllIds())
        {
            TraySupport.Destroy(id);
        }


        // 先正常关窗口，让 closing 事件能发出去——Emit 会检查 _closed，
        // 所以不能像以前那样先把 _closed 置位再退出。
        try
        {
            _window.Close();
        }
        catch (Exception ex)
        {
            HostLog.Write("close window failed: " + ex.Message);
        }

        if (_closed)
        {
            // Closed 事件已经把收尾做完（正常用户关窗走的就是这条路）
            return;
        }

        _closed = true;

        try
        {
            _channel.Send(Outbound.Bye(code));
        }
        catch
        {
        }

        _channel.Dispose();
        Application.Current.Exit();
    }
}
