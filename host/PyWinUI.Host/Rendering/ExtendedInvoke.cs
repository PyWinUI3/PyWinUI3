using System.Text.Json.Nodes;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace PyWinUI.Host.Rendering;

/// <summary>
/// 扩展 invoke 命令。Session 的主 switch 处理不了的都落到这里，
/// 返回 false 才是真正的「未知方法」。
/// </summary>
internal static class ExtendedInvoke
{
    public static bool TryInvoke(
        FrameworkElement? element,
        string method,
        JsonObject? args,
        string nodeId,
        Action<string> warn,
        Action<string, string, JsonObject?, bool> emit)
    {
        if (element is null)
        {
            return false;
        }

        switch (method)
        {
            // ---------------- Win2D 画布 ----------------
            case "update_shapes":
                if (element is CanvasControl canvas2d && args?["updates"] is JsonArray shapeUpdates)
                {
                    Canvas2DSupport.UpdateShapes(canvas2d, shapeUpdates);
                }
                return true;

            // ---------------- 文本选区 ----------------
            case "select_range":
                if (element is TextBox rangeBox)
                {
                    var start = ValueParsers.AsInt(args?["start"]) ?? 0;
                    var length = ValueParsers.AsInt(args?["length"]) ?? 0;
                    start = Math.Clamp(start, 0, rangeBox.Text.Length);
                    length = Math.Clamp(length, 0, rangeBox.Text.Length - start);
                    rangeBox.Select(start, length);
                }
                return true;

            case "clear":
                switch (element)
                {
                    case TextBox clearBox: clearBox.Text = ""; break;
                    case PasswordBox clearPwd: clearPwd.Password = ""; break;
                    case AutoSuggestBox clearSuggest: clearSuggest.Text = ""; break;
                    case RichEditBox clearRich:
                        clearRich.Document.SetText(Microsoft.UI.Text.TextSetOptions.None, "");
                        break;
                }
                return true;

            case "copy":
                if (element is TextBox copyBox) copyBox.CopySelectionToClipboard();
                return true;
            case "cut":
                if (element is TextBox cutBox) cutBox.CutSelectionToClipboard();
                return true;
            case "paste":
                if (element is TextBox pasteBox) pasteBox.PasteFromClipboard();
                return true;

            // ---------------- 显示 / 隐藏 ----------------
            case "show":
            case "hide":
                var visible = method == "show";
                switch (element)
                {
                    case InfoBar info: info.IsOpen = visible; break;
                    case Expander expander: expander.IsExpanded = visible; break;
                    case NavigationView nav: nav.IsPaneOpen = visible; break;
                    case ContentDialog dialog: if (!visible) dialog.Hide(); break;
                }
                return true;

            // ---------------- 自动收缩侧栏 ----------------
            case "set_open":
                if (ValueParsers.AsBool(args?["open"]) is { } open)
                {
                    AutoCollapse.SetOpen(element, open);
                }
                return true;

            case "release":
                AutoCollapse.Release(element);
                return true;

            // ---------------- 滚动 ----------------
            case "scroll_into_view":
                var index = ValueParsers.AsInt(args?["index"]) ?? 0;
                if (element is ItemsControl items && index >= 0 && index < items.Items.Count)
                {
                    (element as ListViewBase)?.ScrollIntoView(items.Items[index]);
                }
                return true;

            // ---------------- WebView2 脚本 ----------------
            // 脚本是异步的，且协议没有「命令返回值」，所以结果用事件回推。
            case "execute_script":
                if (element is WebView2 web && ValueParsers.AsString(args?["script"]) is { } script)
                {
                    _ = RunScriptAsync(web, script, nodeId, ValueParsers.AsString(args?["requestId"]), emit);
                }
                return true;

            case "post_web_message":
                if (element is WebView2 messageWeb && args?["message"] is { } payload)
                {
                    _ = WebView2Support.WithCore(messageWeb, core =>
                    {
                        if (payload is JsonValue scalar && scalar.TryGetValue<string>(out var text))
                        {
                            core.PostWebMessageAsString(text);
                        }
                        else
                        {
                            core.PostWebMessageAsJson(payload.ToJsonString());
                        }
                    }, warn);
                }
                return true;

            case "open_devtools":
                if (element is WebView2 devWeb)
                {
                    _ = WebView2Support.WithCore(devWeb, core => core.OpenDevToolsWindow(), warn);
                }
                return true;

            case "respond_new_window":
                if (element is WebView2 windowWeb
                    && ValueParsers.AsString(args?["requestId"]) is { } windowId)
                {
                    WebView2Support.RespondNewWindow(
                        windowWeb,
                        windowId,
                        ValueParsers.AsBool(args?["allow"]) ?? false,
                        ValueParsers.AsString(args?["url"]));
                }
                return true;

            case "respond_download":
                if (element is WebView2 downloadWeb
                    && ValueParsers.AsString(args?["downloadId"]) is { } downloadId)
                {
                    WebView2Support.RespondDownload(
                        downloadWeb,
                        downloadId,
                        ValueParsers.AsBool(args?["allow"]) ?? true,
                        ValueParsers.AsString(args?["filePath"]));
                }
                return true;

            case "respond_permission":
                if (element is WebView2 permWeb
                    && ValueParsers.AsString(args?["requestId"]) is { } permId)
                {
                    WebView2Support.RespondPermission(
                        permWeb,
                        permId,
                        ValueParsers.AsBool(args?["allow"]));
                }
                return true;

            case "respond_certificate_error":
                if (element is WebView2 certWeb
                    && ValueParsers.AsString(args?["requestId"]) is { } certId)
                {
                    WebView2Support.RespondCertificateError(
                        certWeb,
                        certId,
                        ValueParsers.AsBool(args?["allow"]) ?? false);
                }
                return true;

            case "respond_resource":
                if (element is WebView2 resWeb
                    && ValueParsers.AsString(args?["requestId"]) is { } resId)
                {
                    WebView2Support.RespondResource(
                        resWeb,
                        resId,
                        ValueParsers.AsString(args?["action"]) ?? "continue",
                        ValueParsers.AsDouble(args?["status"]) is { } st ? (int)st : null,
                        ValueParsers.AsString(args?["contentType"]),
                        ValueParsers.AsString(args?["body"]));
                }
                return true;

            case "download_action":
                if (element is WebView2 actionWeb
                    && ValueParsers.AsString(args?["downloadId"]) is { } actionId
                    && ValueParsers.AsString(args?["action"]) is { } action)
                {
                    WebView2Support.DownloadAction(actionWeb, actionId, action);
                }
                return true;

            default:
                return false;
        }
    }

    private static async Task RunScriptAsync(
        WebView2 web,
        string script,
        string nodeId,
        string? requestId,
        Action<string, string, JsonObject?, bool> emit)
    {
        try
        {
            if (web.CoreWebView2 is null)
            {
                await WebView2Runtime.EnsureCore(web);
            }

            var json = await web.CoreWebView2!.ExecuteScriptAsync(script);
            emit(nodeId, "scriptResult", WithRequestId(new JsonObject
            {
                ["result"] = json,
            }, requestId), false);
        }
        catch (Exception ex)
        {
            emit(nodeId, "scriptResult", WithRequestId(new JsonObject
            {
                ["error"] = ex.Message,
            }, requestId), false);
        }
    }

    private static JsonObject WithRequestId(JsonObject data, string? requestId)
    {
        if (!string.IsNullOrEmpty(requestId))
        {
            data["requestId"] = requestId;
        }

        return data;
    }

}