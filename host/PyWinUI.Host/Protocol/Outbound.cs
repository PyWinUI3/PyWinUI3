using System.Text.Json;
using System.Text.Json.Nodes;

namespace PyWinUI.Host.Protocol;

/// <summary>宿主运行时环境的回报。</summary>
public sealed record OsInfo(string Version, int Build);

/// <summary>
/// 宿主能力位。Python 侧据此决定要不要请求 Mica 这类 Win11 专属效果。
/// </summary>
public sealed record CapabilitiesInfo(bool Mica, bool Acrylic, bool RoundedCorners, string Backdrop);

/// <summary>
/// Host → Python 的消息构造。每个方法返回**单行 JSON**（不含换行符），
/// 由 Transport 层负责加分隔符并写出。
/// </summary>
public static class Outbound
{
    public static string Ready(OsInfo os, CapabilitiesInfo capabilities) => Serialize(new
    {
        t = "ready",
        protocol = Wire.Version,
        host = Wire.HostId,
        os = new { version = os.Version, build = os.Build },
        capabilities,
    });

    public static string Ack(long rev) => Serialize(new { t = "ack", rev });

    public static string Event(
        string id, string eventName, long rev, JsonObject? data, bool echo, string? session = null) => Serialize(new
    {
        t = "event",
        // main 会话不输出，保持与旧版 Python 完全一样的报文形状
        session = Normalize(session),
        id,
        @event = eventName,
        rev,
        data = SanitizeNonFinite(data),
        // 只在为 true 时输出，避免每条事件都带一个 false 字段
        echo = echo ? true : (bool?)null,
    });

    /// <summary>
    /// query 的应答。value 允许是任意 JSON 值（数字、字符串、布尔、数组、对象、null）。
    /// 失败时 ok=false 并带 error，Python 侧据此抛异常而不是静默返回 None。
    /// </summary>
    public static string Result(
        string id, bool ok, JsonNode? value, string? error = null, string? session = null) => Serialize(new
    {
        t = "result",
        session = Normalize(session),
        id,
        ok,
        value = SanitizeNonFinite(value),
        error = ok ? null : error,
    });

    /// <summary>
    /// 非致命诊断。属性名拼错、未知控件类型、事件名写错这类问题不会让渲染失败，
    /// 但只写进 %TEMP% 日志等于没告诉用户——所以单独回传一条，Python 侧默认打到 stderr。
    /// </summary>
    public static string Warning(string message, string? id = null, string? session = null) => Serialize(new
    {
        t = "warning",
        session = Normalize(session),
        message,
        id,
    });

    /// <summary>micro 程序执行完毕的回执。只有请求带 notify=true 时才发。</summary>
    public static string MicroDone(string id, bool ok, string? error = null) => Serialize(new
    {
        t = "microDone",
        id,
        ok,
        error,
    });

    /// <summary>
    /// 文件对话框的应答。ok=false 表示用户取消；paths 是选中的路径列表
    /// （single/save/folder 恒为 1 项，openMultiple 可以是多项或空）。
    /// </summary>
    public static string FileDialogResult(string id, bool ok, IReadOnlyList<string>? paths, string? error = null) =>
        Serialize(new
        {
            t = "fileDialogResult",
            id,
            ok,
            paths = SanitizePaths(ok ? paths : null),
            error = ok ? null : error,
        });

    private static JsonArray? SanitizePaths(IReadOnlyList<string>? paths)
    {
        if (paths is null)
        {
            return null;
        }

        var array = new JsonArray();
        foreach (var path in paths)
        {
            array.Add(path);
        }

        return array;
    }

    public static string Error(string code, string message, string? id = null, string? session = null) => Serialize(new
    {
        t = "error",
        session = Normalize(session),
        code,
        message,
        id,
    });

    public static string Bye(int code) => Serialize(new { t = "bye", code });

    /// <summary>某个会话窗口关闭：只影响那一扇窗，不终止宿主。</summary>
    public static string WindowClosed(string session, int code) => Serialize(new
    {
        t = "windowClosed",
        session,
        code,
    });

    /// <summary>
    /// 把 NaN / Infinity 换成 null。
    ///
    /// JSON 没有 NaN：System.Text.Json 遇到非有限 double 会直接抛异常。
    /// 最典型的来源是 NumberBox 的空值（Value 就是 double.NaN），
    /// 以前 query("value") 只能回 ok=false；现在回 null，语义也更准。
    /// 事件载荷同样处理，避免 Slider 之类的值把整条 event 打废。
    /// </summary>
    private static JsonNode? SanitizeNonFinite(JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        var clone = node.DeepClone();

        // 顶层就是 NaN 的情况（NumberBox.Value 就是）：Fix 只遍历容器，
        // 不先判一次的话，这个值会原样进 JsonSerializer 然后抛异常。
        if (IsNonFinite(clone))
        {
            return null;
        }

        Fix(clone);
        return clone;
    }

    private static void Fix(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(pair => pair.Key).ToList())
                {
                    var child = obj[key];
                    if (IsNonFinite(child))
                    {
                        obj[key] = null;
                    }
                    else if (child is not null)
                    {
                        Fix(child);
                    }
                }

                break;

            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    var child = array[i];
                    if (IsNonFinite(child))
                    {
                        array[i] = null;
                    }
                    else if (child is not null)
                    {
                        Fix(child);
                    }
                }

                break;
        }
    }

    private static bool IsNonFinite(JsonNode? node) =>
        node is JsonValue value
        && value.TryGetValue<double>(out var number)
        && !double.IsFinite(number);

    /// <summary>main 会话不写 session 字段，保持旧报文形状。</summary>
    private static string? Normalize(string? session) =>
        string.IsNullOrEmpty(session) || session == "main" ? null : session;

    private static string Serialize<T>(T value) =>
        JsonSerializer.Serialize(value, Wire.Json);
}
