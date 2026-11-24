using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Windows.Foundation;

namespace PyWinUI.Host.Rendering;

/// <summary>
/// WebView2 的 CoreWebView2 能力集合。
///
/// 为什么要单独一层：CoreWebView2 是**异步初始化**的，属性经常在它就绪之前
/// 就写进来了；而这些事件（消息、下载、新窗口、进程崩溃）全挂在 Core 上，
/// 不在 XAML 控件上。这里统一做三件事：
/// 1. 缓存 Core 就绪前的设置，初始化完成后补写；
/// 2. 只挂一次 Core 事件，按 Python 实际订阅的事件名过滤回传；
/// 3. 管理需要 Python 回执的 deferral（新窗口 / 下载）和下载操作生命周期。
///
/// 降级占位（Border）不会走到这里，所有方法都要求真的是 WebView2。
/// </summary>
internal static class WebView2Support
{
    private static readonly ConditionalWeakTable<WebView2, State> States = new();
    private static long _nextId;

    private static readonly string[] SettingKeys =
    {
        "isMuted", "userAgent", "isScriptEnabled",
        "areDefaultContextMenusEnabled", "isWebMessageEnabled", "isZoomControlEnabled",
    };

    private static readonly string[] CoreEventNames =
    {
        "webMessage", "contentLoading", "domContentLoaded", "processFailed",
        "newWindowRequested", "downloadStarting", "downloadProgress", "downloadStateChanged",
        "permissionRequested", "webResourceRequested", "certificateError",
    };

    private sealed class State
    {
        public WebView2 Web = null!;
        public string NodeId = "";
        public Action<string, string, JsonObject?, bool> Emit = null!;
        public Action<string> Warn = null!;
        public readonly HashSet<string> Events = new(StringComparer.Ordinal);
        public readonly Dictionary<string, JsonNode?> Pending = new(StringComparer.Ordinal);
        public readonly Dictionary<string, NewWindowRequest> Windows = new(StringComparer.Ordinal);
        public readonly Dictionary<string, DownloadEntry> Downloads = new(StringComparer.Ordinal);
        public readonly Dictionary<string, PermissionRequest> Permissions = new(StringComparer.Ordinal);
        public readonly Dictionary<string, ResourceRequest> Resources = new(StringComparer.Ordinal);
        public readonly Dictionary<string, CertificateErrorRequest> Certificates = new(StringComparer.Ordinal);
        public bool CoreWired;
    }

    /// <summary>
    /// 一次证书错误请求的 deferral。和权限/新窗口一样，把决定权交给 Python：
    /// 默认显示 WebView2 的 TLS 错误页，只有显式回执 allow 才放行。
    /// </summary>
    private sealed class CertificateErrorRequest
    {
        public WebView2 Web = null!;
        public CoreWebView2ServerCertificateErrorDetectedEventArgs Args = null!;
        public Deferral Deferral = null!;
        public DispatcherTimer? Timer;
    }

    private sealed class NewWindowRequest
    {
        public WebView2 Web = null!;
        public CoreWebView2NewWindowRequestedEventArgs Args = null!;
        public Deferral Deferral = null!;
        public DispatcherTimer? Timer;
    }

    private sealed class PermissionRequest
    {
        public WebView2 Web = null!;
        public CoreWebView2PermissionRequestedEventArgs Args = null!;
        public Deferral Deferral = null!;
        public DispatcherTimer? Timer;
    }

    private sealed class ResourceRequest
    {
        public WebView2 Web = null!;
        public CoreWebView2WebResourceRequestedEventArgs Args = null!;
        public Deferral Deferral = null!;
        public DispatcherTimer? Timer;
    }

    private sealed class DownloadEntry
    {
        public string Id = "";
        public State State = null!;
        public CoreWebView2DownloadStartingEventArgs Args = null!;
        public CoreWebView2DownloadOperation Operation = null!;
        public Deferral Deferral = null!;
        public DispatcherTimer? Timer;
        public long LastProgressTick;
        public bool Started;
    }

    // ------------------------------------------------------------------
    // 生命周期
    // ------------------------------------------------------------------

    public static bool IsSettingKey(string key) => SettingKeys.Contains(key);

    public static bool IsCoreEvent(string name) => CoreEventNames.Contains(name);

    public static void Attach(
        WebView2 web,
        string nodeId,
        Action<string, string, JsonObject?, bool> emit,
        Action<string> warn)
    {
        var state = States.GetOrCreateValue(web);
        state.Web = web;
        state.NodeId = nodeId;
        state.Emit = emit;
        state.Warn = warn;

        if (state.CoreWired)
        {
            return;
        }

        web.CoreWebView2Initialized += (_, _) => OnUi(web, () => WireCore(web));
        if (web.CoreWebView2 is not null)
        {
            WireCore(web);
        }
    }

    public static void Subscribe(WebView2 web, string eventName)
    {
        if (States.TryGetValue(web, out var state))
        {
            state.Events.Add(eventName);
        }
    }

    private static void WireCore(WebView2 web)
    {
        if (!States.TryGetValue(web, out var state) || state.CoreWired)
        {
            return;
        }

        var core = web.CoreWebView2;
        if (core is null)
        {
            return;
        }

        state.CoreWired = true;
        foreach (var pair in state.Pending)
        {
            ApplySetting(web, core, pair.Key, pair.Value, null);
        }

        core.WebMessageReceived += (_, e) =>
        {
            var raw = e.WebMessageAsJson ?? "";
            JsonNode? parsed = null;
            try
            {
                parsed = JsonNode.Parse(raw);
            }
            catch (Exception)
            {
                // 不是 JSON 就按字符串给，别让一条消息把事件链断掉
            }

            string? text = null;
            try
            {
                // WinRT 投影里这个方法直接返回字符串，不是 out 参数形式
                text = e.TryGetWebMessageAsString();
            }
            catch (Exception)
            {
                // 用 JSON 对象发消息时这个调用会抛，忽略即可
            }

            Emit(state, "webMessage", new JsonObject
            {
                ["data"] = parsed?.DeepClone(),
                ["raw"] = raw,
                ["text"] = text,
            });
        };

        core.ContentLoading += (_, e) => Emit(state, "contentLoading", new JsonObject
        {
            ["url"] = core.Source ?? "",
            ["isErrorPage"] = e.IsErrorPage,
            ["navigationId"] = e.NavigationId,
        });

        core.DOMContentLoaded += (_, e) => Emit(state, "domContentLoaded", new JsonObject
        {
            ["url"] = core.Source ?? "",
            ["navigationId"] = e.NavigationId,
        });

        core.ProcessFailed += (_, e) => Emit(state, "processFailed", new JsonObject
        {
            ["kind"] = e.ProcessFailedKind.ToString(),
            ["reason"] = e.Reason.ToString(),
            ["exitCode"] = e.ExitCode,
            ["processDescription"] = e.ProcessDescription ?? "",
        });

        core.NewWindowRequested += (_, e) => OnNewWindow(web, state, e);
        core.DownloadStarting += (_, e) => OnDownloadStarting(web, state, e);
        core.PermissionRequested += (_, e) => OnPermissionRequested(web, state, e);
        core.WebResourceRequested += (_, e) => OnWebResourceRequested(web, state, e);
        // 证书错误默认显示 WebView2 的错误页；Python 可订阅 certificateError
        // 事件后按请求回执 allow，实现「可选的逐次放行」而不是全局关闭校验。
        core.ServerCertificateErrorDetected += (_, e) => OnCertificateError(web, state, e);
    }

    private static void Emit(State state, string eventName, JsonObject data)
    {
        if (!state.Events.Contains(eventName))
        {
            return;
        }

        OnUi(state.Web, () => state.Emit(state.NodeId, eventName, data, false));
    }

    private static void OnUi(WebView2 web, Action action)
    {
        var queue = web.DispatcherQueue;
        if (queue is not null && !queue.HasThreadAccess)
        {
            queue.TryEnqueue(() => action());
        }
        else
        {
            action();
        }
    }

    private static string NextId(string prefix) =>
        $"{prefix}{Interlocked.Increment(ref _nextId)}";

    private static DispatcherTimer OneShot(TimeSpan delay, Action action)
    {
        var timer = new DispatcherTimer { Interval = delay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            action();
        };
        timer.Start();
        return timer;
    }

    // ------------------------------------------------------------------
    // 设置（Core 未就绪时先缓存）
    // ------------------------------------------------------------------

    public static void SetSetting(WebView2 web, string key, JsonNode? value, Action<string> warn)
    {
        var state = States.GetOrCreateValue(web);
        state.Pending[key] = value?.DeepClone();

        var core = web.CoreWebView2;
        if (core is not null)
        {
            ApplySetting(web, core, key, value, warn);
        }
    }

    private static void ApplySetting(
        WebView2 web,
        CoreWebView2 core,
        string key,
        JsonNode? value,
        Action<string>? warn)
    {
        try
        {
            switch (key)
            {
                // WinUI 3 的 WebView2 投影没有暴露 CoreWebView2Controller，
                // 因此不提供程序化 zoomFactor；isZoomControlEnabled 只控制
                // 用户能否用 Ctrl+滚轮缩放。不做「CSS zoom」假实现。
                case "isMuted":
                    if (ValueParsers.AsBool(value) is { } muted)
                    {
                        core.IsMuted = muted;
                    }

                    break;
                case "userAgent":
                    if (ValueParsers.AsString(value) is { } agent)
                    {
                        core.Settings.UserAgent = agent;
                    }

                    break;
                case "isScriptEnabled":
                    if (ValueParsers.AsBool(value) is { } script)
                    {
                        core.Settings.IsScriptEnabled = script;
                    }

                    break;
                case "areDefaultContextMenusEnabled":
                    if (ValueParsers.AsBool(value) is { } menus)
                    {
                        core.Settings.AreDefaultContextMenusEnabled = menus;
                    }

                    break;
                case "isWebMessageEnabled":
                    if (ValueParsers.AsBool(value) is { } message)
                    {
                        core.Settings.IsWebMessageEnabled = message;
                    }

                    break;
                case "isZoomControlEnabled":
                    if (ValueParsers.AsBool(value) is { } control)
                    {
                        core.Settings.IsZoomControlEnabled = control;
                    }

                    break;
            }
        }
        catch (Exception ex)
        {
            warn?.Invoke($"WebView2 设置 {key} 失败: {ex.Message}");
        }
    }

    public static JsonNode? GetSetting(WebView2 web, string key)
    {
        var state = States.GetOrCreateValue(web);
        var core = web.CoreWebView2;
        if (core is null)
        {
            return state.Pending.TryGetValue(key, out var pending) ? pending?.DeepClone() : null;
        }

        return key switch
        {
            "isMuted" => core.IsMuted,
            "userAgent" => core.Settings.UserAgent,
            "isScriptEnabled" => core.Settings.IsScriptEnabled,
            "areDefaultContextMenusEnabled" => core.Settings.AreDefaultContextMenusEnabled,
            "isWebMessageEnabled" => core.Settings.IsWebMessageEnabled,
            "isZoomControlEnabled" => core.Settings.IsZoomControlEnabled,
            _ => null,
        };
    }

    // ------------------------------------------------------------------
    // 新窗口：Python 回执决定
    // ------------------------------------------------------------------

    private static void OnNewWindow(
        WebView2 web,
        State state,
        CoreWebView2NewWindowRequestedEventArgs args)
    {
        // GetDeferral 必须同步调用，否则事件处理器一返回就按默认行为弹窗了
        var deferral = args.GetDeferral();
        var id = NextId("w");
        var entry = new NewWindowRequest
        {
            Web = web,
            Args = args,
            Deferral = deferral,
        };
        state.Windows[id] = entry;

        OnUi(web, () =>
        {
            if (!state.Events.Contains("newWindowRequested"))
            {
                // 没挂回调 = 浏览器最自然的默认行为：在原视图里打开目标地址。
                // 之前是拒绝（window.open 静默失效），对浏览器场景不对。
                RespondNewWindow(web, id, true, null);
                return;
            }

            Emit(state, "newWindowRequested", new JsonObject
            {
                ["requestId"] = id,
                ["url"] = args.Uri ?? "",
                ["name"] = args.Name ?? "",
                ["isUserInitiated"] = args.IsUserInitiated,
            });

            // 不回执不能让 deferral 永远挂着：超时按「拒绝」处理
            entry.Timer = OneShot(
                TimeSpan.FromSeconds(8),
                () => RespondNewWindow(web, id, false, null));
        });
    }

    public static void RespondNewWindow(WebView2 web, string id, bool allow, string? url)
    {
        if (!States.TryGetValue(web, out var state)
            || !state.Windows.TryGetValue(id, out var entry))
        {
            return;
        }

        state.Windows.Remove(id);
        entry.Timer?.Stop();

        OnUi(web, () =>
        {
            try
            {
                // 只有「允许但没指定 new window」时才会弹系统窗口；
                // 当前模型是单 WebView，允许 = 在当前视图导航过去。
                entry.Args.Handled = true;
                if (allow
                    && Uri.TryCreate(string.IsNullOrWhiteSpace(url) ? entry.Args.Uri : url,
                        UriKind.Absolute, out var target))
                {
                    web.Source = target;
                }
            }
            finally
            {
                entry.Deferral.Complete();
            }
        });
    }

    // ------------------------------------------------------------------
    // 权限：Python 可选回执
    // ------------------------------------------------------------------

    private static void OnPermissionRequested(
        WebView2 web, State state, CoreWebView2PermissionRequestedEventArgs args)
    {
        var deferral = args.GetDeferral();
        var id = NextId("p");
        var entry = new PermissionRequest
        {
            Web = web,
            Args = args,
            Deferral = deferral,
        };
        state.Permissions[id] = entry;

        OnUi(web, () =>
        {
            if (!state.Events.Contains("permissionRequested"))
            {
                // 没挂回调：保留系统默认行为（WebView2 自己按站点权限决定）
                CompletePermission(web, id, null);
                return;
            }

            Emit(state, "permissionRequested", new JsonObject
            {
                ["requestId"] = id,
                ["kind"] = args.PermissionKind.ToString(),
                ["uri"] = args.Uri ?? "",
                ["isUserInitiated"] = args.IsUserInitiated,
            });

            // 8 秒没回执：保持默认，不让 deferral 永远挂着
            entry.Timer = OneShot(
                TimeSpan.FromSeconds(8),
                () => CompletePermission(web, id, null));
        });
    }

    /// <summary>allow=null 表示不干预，交给 WebView2 默认行为。</summary>
    public static void RespondPermission(WebView2 web, string id, bool? allow)
    {
        if (!States.TryGetValue(web, out var state)
            || !state.Permissions.TryGetValue(id, out var entry))
        {
            return;
        }

        state.Permissions.Remove(id);
        entry.Timer?.Stop();
        CompletePermission(web, id, allow, entry);
    }

    private static void CompletePermission(WebView2 web, string id, bool? allow, PermissionRequest? entry = null)
    {
        if (entry is null
            && (!States.TryGetValue(web, out var s) || !s.Permissions.Remove(id)))
        {
            return;
        }

        entry ??= States.TryGetValue(web, out var s2)
            && s2.Permissions.TryGetValue(id, out var e) ? e : null;
        if (entry is null)
        {
            return;
        }

        OnUi(web, () =>
        {
            try
            {
                if (allow is { } decision)
                {
                    entry.Args.State = decision
                        ? CoreWebView2PermissionState.Allow
                        : CoreWebView2PermissionState.Deny;
                }
            }
            finally
            {
                entry.Deferral.Complete();
            }
        });
    }

    // ------------------------------------------------------------------
    // 证书错误：默认拒绝，Python 逐次可选放行
    // ------------------------------------------------------------------

    private static void OnCertificateError(
        WebView2 web,
        State state,
        CoreWebView2ServerCertificateErrorDetectedEventArgs args)
    {
        // 必须同步调用 GetDeferral，否则事件处理返回后 WebView2 立即按默认行为
        // 显示 TLS 错误页，Python 还没收到告警就已经定性了。
        var deferral = args.GetDeferral();
        var id = NextId("cert");
        var entry = new CertificateErrorRequest
        {
            Web = web,
            Args = args,
            Deferral = deferral,
        };
        state.Certificates[id] = entry;

        var url = args.RequestUri ?? "";
        var error = args.ErrorStatus.ToString();

        // 无论有没有订阅事件，都先给一条告警——证书问题不能静默吞掉。
        // 这是替代「全局 --ignore-certificate-errors」的关键：默认拒绝 +
        // 可见告警，放行要显式回执。
        OnUi(web, () =>
        {
            state.Warn($"WebView2 证书错误 [{error}] {url}：" +
                       "默认已拒绝并显示错误页；如确认可信，可绑定 certificateError " +
                       $"事件后用 respond_certificate_error(\"{id}\", True) 放行");

            if (!state.Events.Contains("certificateError"))
            {
                // 没订阅：保留 WebView2 默认行为（显示 TLS 错误页）
                CompleteCertificate(web, id, null);
                return;
            }

            Emit(state, "certificateError", new JsonObject
            {
                ["requestId"] = id,
                ["url"] = url,
                ["errorStatus"] = error,
            });

            // 8 秒没回执：保持默认拒绝，不让 deferral 永远挂着
            entry.Timer = OneShot(
                TimeSpan.FromSeconds(8),
                () => CompleteCertificate(web, id, null));
        });
    }

    /// <summary>allow=true 放行这一次证书错误；false/null 保持默认拒绝。</summary>
    public static void RespondCertificateError(WebView2 web, string id, bool allow)
    {
        if (!States.TryGetValue(web, out var state)
            || !state.Certificates.TryGetValue(id, out var entry))
        {
            return;
        }

        state.Certificates.Remove(id);
        entry.Timer?.Stop();
        CompleteCertificate(web, id, allow, entry);
    }

    private static void CompleteCertificate(
        WebView2 web, string id, bool? allow, CertificateErrorRequest? entry = null)
    {
        // 先取到 entry 再移除，不能像「先 Remove 再查」那样写——那样第二次
        // 查不到，deferral 就永远挂着，页面会卡在加载态。
        var request = entry;
        if (request is null)
        {
            if (!States.TryGetValue(web, out var s)
                || !s.Certificates.TryGetValue(id, out request))
            {
                return;
            }
            s.Certificates.Remove(id);
        }

        var captured = request;
        OnUi(web, () =>
        {
            try
            {
                if (allow == true)
                {
                    captured.Args.Action = CoreWebView2ServerCertificateErrorAction.AlwaysAllow;
                }
                // null / false：不设置 Action，保留 WebView2 默认的 TLS 错误页
            }
            finally
            {
                captured.Deferral.Complete();
            }
        });
    }

    // ------------------------------------------------------------------
    // 资源拦截：Python 可选回执
    // ------------------------------------------------------------------

    private static void OnWebResourceRequested(
        WebView2 web, State state, CoreWebView2WebResourceRequestedEventArgs args)
    {
        // 没挂回调就不设 AddWebResourceRequestedFilter，事件根本不会触发；
        // 走到这里说明有订阅（或订阅后取消），还是按订阅状态分流。
        var deferral = args.GetDeferral();
        var id = NextId("r");
        var entry = new ResourceRequest
        {
            Web = web,
            Args = args,
            Deferral = deferral,
        };
        state.Resources[id] = entry;

        OnUi(web, () =>
        {
            if (!state.Events.Contains("webResourceRequested"))
            {
                CompleteResource(web, id, null, null, null, null, entry);
                return;
            }

            var req = args.Request;
            Emit(state, "webResourceRequested", new JsonObject
            {
                ["requestId"] = id,
                ["url"] = req?.Uri ?? "",
                ["method"] = req?.Method ?? "",
                ["headers"] = HeaderObject(req?.Headers),
                ["resourceContext"] = args.ResourceContext.ToString(),
            });

            entry.Timer = OneShot(
                TimeSpan.FromSeconds(8),
                () => CompleteResource(web, id, null, null, null, null, entry));
        });
    }

    private static JsonObject HeaderObject(CoreWebView2HttpRequestHeaders? headers)
    {
        var obj = new JsonObject();
        if (headers is null)
        {
            return obj;
        }

        try
        {
            foreach (var pair in headers)
            {
                obj[pair.Key] = pair.Value;
            }
        }
        catch
        {
            // 枚举头文件失败不该影响请求本身
        }

        return obj;
    }

    /// <summary>回执：action = continue / cancel / respond；后两者带 status/contentType/body。</summary>
    public static void RespondResource(
        WebView2 web, string id, string action,
        int? status, string? contentType, string? body)
    {
        if (!States.TryGetValue(web, out var state)
            || !state.Resources.TryGetValue(id, out var entry))
        {
            return;
        }

        state.Resources.Remove(id);
        entry.Timer?.Stop();
        CompleteResource(web, id, action, status, contentType, body, entry);
    }

    private static void CompleteResource(
        WebView2 web, string id,
        string? action, int? status, string? contentType, string? body,
        ResourceRequest? entry = null)
    {
        if (entry is null
            && (!States.TryGetValue(web, out var s) || !s.Resources.Remove(id)))
        {
            return;
        }

        entry ??= States.TryGetValue(web, out var s2)
            && s2.Resources.TryGetValue(id, out var e) ? e : null;
        if (entry is null)
        {
            return;
        }

        OnUi(web, () =>
        {
            try
            {
                if (action is "cancel")
                {
                    // 用空响应 + 失败状态码取消请求
                    ApplyResponse(entry.Args, 403, "text/plain", "");
                }
                else if (action is "respond")
                {
                    ApplyResponse(
                        entry.Args,
                        status ?? 200,
                        contentType ?? "text/plain",
                        body ?? "");
                }
                // continue / null：不改 Response，交给网络栈
            }
            catch (Exception ex)
            {
                HostLog.Write($"[webview2] 资源回执 {id} 失败: {ex.Message}");
            }
            finally
            {
                entry.Deferral.Complete();
            }
        });
    }

    private static void ApplyResponse(
        CoreWebView2WebResourceRequestedEventArgs args,
        int status, string contentType, string body)
    {
        // WebResourceRequested 的 args.Response 默认就是一个可写的空响应，
        // 直接改它的字段即可——WinUI 3 投影没有 CreateWebResourceResponse。
        var response = args.Response;
        if (response is null)
        {
            return;
        }

        response.StatusCode = status;
        response.ReasonPhrase = ReasonPhrase(status);
        response.Content = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(body))
            .AsRandomAccessStream();
        // WinUI 3 投影里 Headers 只有 AppendHeader 可写
        response.Headers.AppendHeader("Content-Type", contentType);
    }

    private static string ReasonPhrase(int status) => status switch
    {
        200 => "OK",
        204 => "No Content",
        301 => "Moved Permanently",
        302 => "Found",
        403 => "Forbidden",
        404 => "Not Found",
        500 => "Internal Server Error",
        _ => "OK",
    };

    // ------------------------------------------------------------------
    // 下载：DownloadOperation 生命周期
    // ------------------------------------------------------------------

    private static void OnDownloadStarting(
        WebView2 web,
        State state,
        CoreWebView2DownloadStartingEventArgs args)
    {
        var deferral = args.GetDeferral();
        var id = NextId("d");
        var entry = new DownloadEntry
        {
            Id = id,
            State = state,
            Args = args,
            Operation = args.DownloadOperation,
            Deferral = deferral,
        };
        state.Downloads[id] = entry;

        OnUi(web, () =>
        {
            if (!state.Events.Contains("downloadStarting"))
            {
                RespondDownload(web, id, true, null);
                return;
            }

            Emit(state, "downloadStarting", new JsonObject
            {
                ["downloadId"] = id,
                ["url"] = args.DownloadOperation.Uri ?? "",
                ["suggestedFilePath"] = args.ResultFilePath ?? "",
                ["totalBytes"] = args.DownloadOperation.TotalBytesToReceive,
            });

            entry.Timer = OneShot(
                TimeSpan.FromSeconds(8),
                () => RespondDownload(web, id, true, null));
        });
    }

    public static void RespondDownload(WebView2 web, string id, bool allow, string? filePath)
    {
        if (!States.TryGetValue(web, out var state)
            || !state.Downloads.TryGetValue(id, out var entry)
            || entry.Started)
        {
            return;
        }

        entry.Started = true;
        entry.Timer?.Stop();

        OnUi(web, () =>
        {
            try
            {
                entry.Args.Handled = true;  // 关掉默认保存对话框
                if (!allow)
                {
                    entry.Args.Cancel = true;
                }
                else if (!string.IsNullOrWhiteSpace(filePath))
                {
                    entry.Args.ResultFilePath = filePath;
                }

                entry.Operation.BytesReceivedChanged += (_, _) => EmitProgress(entry);
                entry.Operation.StateChanged += (_, _) => EmitState(entry);
            }
            finally
            {
                entry.Deferral.Complete();
            }

            EmitProgress(entry, force: true);
            EmitState(entry);
        });
    }

    public static void DownloadAction(WebView2 web, string id, string action)
    {
        if (!States.TryGetValue(web, out var state)
            || !state.Downloads.TryGetValue(id, out var entry)
            || !entry.Started)
        {
            return;
        }

        OnUi(web, () =>
        {
            try
            {
                switch (action)
                {
                    case "cancel": entry.Operation.Cancel(); break;
                    case "pause": entry.Operation.Pause(); break;
                    case "resume": entry.Operation.Resume(); break;
                }
            }
            catch (Exception ex)
            {
                HostLog.Write($"[webview2] 下载 {id} 执行 {action} 失败: {ex.Message}");
            }
        });
    }

    private static void EmitProgress(DownloadEntry entry, bool force = false)
    {
        var now = Environment.TickCount64;
        if (!force && now - entry.LastProgressTick < 100)
        {
            return;  // BytesReceivedChanged 非常密，100ms 节流
        }

        entry.LastProgressTick = now;
        Emit(entry.State, "downloadProgress", new JsonObject
        {
            ["downloadId"] = entry.Id,
            ["bytesReceived"] = entry.Operation.BytesReceived,
            ["totalBytes"] = entry.Operation.TotalBytesToReceive,
            ["state"] = entry.Operation.State.ToString(),
        });
    }

    private static void EmitState(DownloadEntry entry)
    {
        var stateName = entry.Operation.State.ToString();
        Emit(entry.State, "downloadStateChanged", new JsonObject
        {
            ["downloadId"] = entry.Id,
            ["state"] = stateName,
            ["interruptReason"] = entry.Operation.InterruptReason.ToString(),
            ["bytesReceived"] = entry.Operation.BytesReceived,
            ["totalBytes"] = entry.Operation.TotalBytesToReceive,
            ["filePath"] = entry.Operation.ResultFilePath ?? "",
            ["url"] = entry.Operation.Uri ?? "",
        });

        if (stateName is "Completed" or "Interrupted")
        {
            entry.State.Downloads.Remove(entry.Id);
        }
    }

    // ------------------------------------------------------------------
    // Core 尚未就绪时的 invoke
    // ------------------------------------------------------------------

    public static async Task WithCore(WebView2 web, Action<CoreWebView2> action, Action<string> warn)
    {
        try
        {
            if (web.CoreWebView2 is null)
            {
                await WebView2Runtime.EnsureCore(web);
            }

            if (web.CoreWebView2 is { } core)
            {
                action(core);
            }
        }
        catch (Exception ex)
        {
            warn($"WebView2 操作失败: {ex.Message}");
        }
    }
}
