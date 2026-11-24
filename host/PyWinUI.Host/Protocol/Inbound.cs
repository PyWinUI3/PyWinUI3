using System.Text.Json;
using System.Text.Json.Nodes;

namespace PyWinUI.Host.Protocol;

/// <summary>Python → Host 的消息。</summary>
public abstract record InboundMessage;

public sealed record HelloMessage : InboundMessage
{
    public required int Protocol { get; init; }
    public required string Client { get; init; }
    public int? Pid { get; init; }
}

public sealed record RenderMessage : InboundMessage
{
    /// <summary>目标会话 id，缺省 main。首帧带新 id 时宿主会新建窗口。</summary>
    public string Session { get; init; } = "main";

    public required long Rev { get; init; }
    public required WindowSpec Window { get; init; }
    public required string Root { get; init; }

    /// <summary>扁平节点列表，父子关系由 NodeSpec.Children 表达。</summary>
    public required List<NodeSpec> Nodes { get; init; }
}

public sealed record PatchMessage : InboundMessage
{
    public string Session { get; init; } = "main";

    public required long Rev { get; init; }

    /// <summary>按数组顺序依次应用，操作之间允许有依赖。</summary>
    public required List<PatchOp> Ops { get; init; }
}

public sealed record InvokeMessage : InboundMessage
{
    public string Session { get; init; } = "main";

    public required string Id { get; init; }
    public required string Method { get; init; }
    public JsonObject? Args { get; init; }
}

/// <summary>
/// 同步读取一个运行时值。invoke 是「让宿主做事」的单向命令，
/// 没有返回值；query 补的是「问宿主要一个值」这半边。
/// </summary>
public sealed record QueryMessage : InboundMessage
{
    public string Session { get; init; } = "main";

    /// <summary>请求 id，回包原样带回，便于并发时配对。</summary>
    public required string Id { get; init; }

    /// <summary>目标节点 id；窗口级查询传根节点 id。</summary>
    public required string Node { get; init; }

    /// <summary>要读的键，如 actualWidth / text / windowState。</summary>
    public required string Key { get; init; }

    public JsonObject? Args { get; init; }
}

/// <summary>弹出模态对话框。主要用于调试和演示，会阻塞窗口交互。</summary>
public sealed record DialogMessage : InboundMessage
{
    public string Session { get; init; } = "main";
    public string Title { get; init; } = "提示";
    public string Body { get; init; } = "";

    /// <summary>info / warn / error</summary>
    public string? Kind { get; init; }
}

/// <summary>
/// 打开文件选择对话框（自绘，不用原生 Picker）。带 id，宿主回一条
/// <c>fileDialogResult</c> 配对——和 query/result 同一个同步读回模型：
/// invoke 只能单向做事，而文件选择必须把用户选中的路径带回来。
/// </summary>
public sealed record FileDialogMessage : InboundMessage
{
    public string Session { get; init; } = "main";
    /// <summary>请求 id，回包原样带回。</summary>
    public required string Id { get; init; }

    /// <summary>open / openMultiple / save / folder。</summary>
    public required string Mode { get; init; }

    public string? Title { get; init; }
    public string? InitialDir { get; init; }
    public string? Filter { get; init; }
    public string? DefaultName { get; init; }
}

/// <summary>托盘图标操作：create / update / destroy。</summary>
public sealed record TrayMessage : InboundMessage
{
    public required string Action { get; init; }
    public required string Id { get; init; }
    public JsonObject? Spec { get; init; }
}

/// <summary>
/// 窗口级控制：hide / show / close / focus。
/// 与 close 协议命令不同，这里只关一个会话窗口，不结束整个宿主。
/// </summary>
public sealed record WindowActionMessage : InboundMessage
{
    public string Session { get; init; } = "main";
    public required string Action { get; init; }
}

public sealed record CloseMessage : InboundMessage
{
    public int Code { get; init; }
}

/// <summary>
/// 一段在宿主 UI 线程内执行的受限指令。
///
/// 这不是可执行 C# 代码：program 只是数据，由 MicroRuntime 按固定语义解释，
/// 指令白名单、数量上限、数值范围、时长上限都在这里和运行时双重校验。
/// 引入它是为了把「每帧一次跨进程往返」的动画改成「一条消息跑完整段」。
/// </summary>
public sealed record MicroMessage : InboundMessage
{
    public string Session { get; init; } = "main";

    public required string Id { get; init; }
    public required string Node { get; init; }
    public required JsonArray Program { get; init; }
    public bool Notify { get; init; }
}

/// <summary>
/// 本宿主不认识的消息类型。协议第 7 章规定「新增 op 不算不兼容变更」，
/// 因此未知 op 不能终止会话：回一条 warning 提醒版本错配，然后忽略。
/// 这样新 Python 配旧宿主时只是新功能不可用，而不是第一条 query 就把会话打死。
/// </summary>
public sealed record UnknownMessage : InboundMessage
{
    public required string Op { get; init; }
}

/// <summary>
/// 入站消息解析。所有校验都在这里做完，渲染层拿到的就是类型化的对象。
/// </summary>
public static class Inbound
{
    public static InboundMessage Parse(string line)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(line);
        }
        catch (JsonException ex)
        {
            throw new ProtocolException("bad-json", "消息不是合法 JSON", inner: ex);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new ProtocolException("bad-protocol", "消息必须是 JSON 对象");

            return RequireString(root, "op") switch
            {
                "hello" => ParseHello(root),
                "render" => ParseRender(root),
                "patch" => ParsePatch(root),
                "invoke" => ParseInvoke(root),
                "query" => ParseQuery(root),
                "micro" => ParseMicro(root),
                "dialog" => ParseDialog(root),
                "fileDialog" => ParseFileDialog(root),
                "close" => ParseClose(root),
                "windowAction" => ParseWindowAction(root),
                "tray" => ParseTray(root),
                // 新增 op 不算不兼容变更，旧端遇到新消息应当忽略而不是终止会话。
                // 字段缺失/类型错误走 bad-protocol：回一条 error 后跳过这一行，
                // 同样不终止——消息逐行分帧，坏一行不影响下一行。
                // 唯一致命的是 version-mismatch（见 Wire.IsFatal）。
                var op => new UnknownMessage { Op = op },
            };
        }
    }

    private static HelloMessage ParseHello(JsonElement e) => new()
    {
        Protocol = (int)RequireLong(e, "protocol"),
        Client = RequireString(e, "client"),
        Pid = (int?)TryLong(e, "pid"),
    };

    private static RenderMessage ParseRender(JsonElement e)
    {
        var nodesElement = RequireArray(e, "nodes");
        var nodes = new List<NodeSpec>(nodesElement.GetArrayLength());
        foreach (var n in nodesElement.EnumerateArray())
            nodes.Add(ParseNode(n));

        return new RenderMessage
        {
            Session = e.TryGetProperty("session", out var sessionElement)
                && sessionElement.ValueKind == JsonValueKind.String
                ? sessionElement.GetString() ?? "main"
                : "main",
            Rev = RequireLong(e, "rev"),
            Window = ParseWindow(RequireElement(e, "window")),
            Root = RequireString(e, "root"),
            Nodes = nodes,
        };
    }

    private static PatchMessage ParsePatch(JsonElement e)
    {
        var opsElement = RequireArray(e, "ops");
        var ops = new List<PatchOp>(opsElement.GetArrayLength());
        foreach (var o in opsElement.EnumerateArray())
            ops.Add(ParseOp(o));

        return new PatchMessage
        {
            Session = TryString(e, "session") ?? "main",
            Rev = RequireLong(e, "rev"),
            Ops = ops,
        };
    }

    private static InvokeMessage ParseInvoke(JsonElement e) => new()
    {
        Session = TryString(e, "session") ?? "main",
        Id = RequireString(e, "id"),
        Method = RequireString(e, "method"),
        Args = e.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Object
            ? ToJsonObject(a)
            : null,
    };

    private static QueryMessage ParseQuery(JsonElement e) => new()
    {
        Session = TryString(e, "session") ?? "main",
        Id = RequireString(e, "id"),
        Node = RequireString(e, "node"),
        Key = RequireString(e, "key"),
        Args = e.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Object
            ? ToJsonObject(a)
            : null,
    };

    private static TrayMessage ParseTray(JsonElement e) => new()
    {
        Action = RequireString(e, "action"),
        Id = RequireString(e, "id"),
        Spec = e.TryGetProperty("spec", out var spec) && spec.ValueKind == JsonValueKind.Object
            ? ToJsonObject(spec)
            : null,
    };

    private static WindowActionMessage ParseWindowAction(JsonElement e) => new()
    {
        Session = TryString(e, "session") ?? "main",
        Action = RequireString(e, "action"),
    };

    private static CloseMessage ParseClose(JsonElement e) => new()
    {
        Code = (int)(TryLong(e, "code") ?? 0),
    };

    /// <summary>
    /// 解析 micro 指令序列。这里只做**形状**校验，语义校验（白名单、数值范围、
    /// 时长上限、节点是否存在）由 MicroRuntime 在 UI 线程再做一次。
    /// 校验失败一律回非致命的 bad-micro，不让一条坏指令终止整个会话。
    /// </summary>
    private static MicroMessage ParseMicro(JsonElement e)
    {
        var id = RequireString(e, "id");
        var node = RequireString(e, "node");
        var ops = RequireArray(e, "program");

        if (ops.GetArrayLength() == 0)
            throw new ProtocolException("bad-micro", "micro program 不能为空", id);
        if (ops.GetArrayLength() > 64)
            throw new ProtocolException("bad-micro", "micro program 超过 64 条指令上限", id);

        var program = new JsonArray();
        foreach (var op in ops.EnumerateArray())
        {
            if (op.ValueKind != JsonValueKind.Object
                || !op.TryGetProperty("i", out var instruction)
                || instruction.ValueKind != JsonValueKind.String)
            {
                throw new ProtocolException("bad-micro", "micro 指令必须是带 i 字段的对象", id);
            }

            // 必须复制：JsonElement 在 using(doc) 结束后就失效了
            program.Add(JsonNode.Parse(op.GetRawText()));
        }

        return new MicroMessage
        {
            // micro 必须按会话路由：非 main 窗口的节点只能由它自己的
            // MicroRuntime 执行，否则找不到节点，甚至错写到 main 的同名节点。
            Session = TryString(e, "session") ?? "main",
            Id = id,
            Node = node,
            Program = program,
            Notify = TryBool(e, "notify") ?? false,
        };
    }

    private static DialogMessage ParseDialog(JsonElement e) => new()
    {
        Session = TryString(e, "session") ?? "main",
        Title = TryString(e, "title") ?? "提示",
        Body = TryString(e, "body") ?? "",
        Kind = TryString(e, "kind"),
    };

    private static FileDialogMessage ParseFileDialog(JsonElement e)
    {
        var rawMode = RequireString(e, "mode").ToLowerInvariant();
        // 模式白名单 + 归一化：拼错就回非致命 bad-protocol，
        // 而不是打开一个行为未知的窗。归一化后 SystemFilePicker 直接用规范值判断。
        var mode = rawMode switch
        {
            "open" => "open",
            "openmultiple" => "openMultiple",
            "save" => "save",
            "folder" => "folder",
            _ => throw new ProtocolException("bad-protocol", $"未知的文件对话框模式 \"{rawMode}\""),
        };

        return new FileDialogMessage
        {
            Session = TryString(e, "session") ?? "main",
            Id = RequireString(e, "id"),
            Mode = mode,
            Title = TryString(e, "title"),
            InitialDir = TryString(e, "initialDir"),
            Filter = TryString(e, "filter"),
            DefaultName = TryString(e, "defaultName"),
        };
    }

    private static WindowSpec ParseWindow(JsonElement e)
    {
        var spec = new WindowSpec();
        spec.Title = TryString(e, "title");
        spec.Width = TryDouble(e, "width");
        spec.Height = TryDouble(e, "height");
        spec.MinWidth = TryDouble(e, "minWidth");
        spec.MinHeight = TryDouble(e, "minHeight");
        spec.Resizable = TryBool(e, "resizable");
        spec.Center = TryBool(e, "center");
        spec.X = TryDouble(e, "x");
        spec.Y = TryDouble(e, "y");
        spec.Icon = TryString(e, "icon");
        spec.AlwaysOnTop = TryBool(e, "alwaysOnTop");
        spec.Minimizable = TryBool(e, "minimizable");
        spec.Maximizable = TryBool(e, "maximizable");
        spec.Maximized = TryBool(e, "maximized");
        // 「RetrunButtom」是早期示例里写错的拼写，收下以免静默失效
        spec.ReturnButton = TryBool(e, "returnButton") ?? TryBool(e, "RetrunButtom");
        spec.TextInputBox = TryBool(e, "textInputBox") ?? TryBool(e, "TextInputBox");
        spec.Scroll = TryBool(e, "scroll");
        spec.Theme = TryString(e, "theme");
        spec.Backdrop = TryString(e, "backdrop");
        spec.Parent = TryString(e, "parent");
        spec.Layer = TryString(e, "layer");
        return spec;
    }

    private static NodeSpec ParseNode(JsonElement e)
    {
        var node = new NodeSpec
        {
            Id = RequireString(e, "id"),
            Type = RequireString(e, "type"),
        };

        if (e.TryGetProperty("props", out var props) && props.ValueKind == JsonValueKind.Object)
            node.Props = ToJsonObject(props);

        if (e.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array)
        {
            node.Children = new List<string>(children.GetArrayLength());
            foreach (var c in children.EnumerateArray())
            {
                if (c.ValueKind != JsonValueKind.String)
                    throw new ProtocolException("bad-protocol", $"节点 {node.Id} 的 children 含非字符串项");
                node.Children.Add(c.GetString()!);
            }
        }

        if (e.TryGetProperty("events", out var events) && events.ValueKind == JsonValueKind.Object)
            node.Events = TryEvents(e, "events");

        return node;
    }

    private static PatchOp ParseOp(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object)
            throw new ProtocolException("bad-op", "patch 操作必须是 JSON 对象");

        var t = RequireString(e, "t");
        return t switch
        {
            "set" => new PatchOp
            {
                Kind = PatchOpKind.Set,
                Id = RequireString(e, "id"),
                Props = RequireObject(e, "props"),
                Events = TryEvents(e, "events"),
            },
            "insert" => new PatchOp
            {
                Kind = PatchOpKind.Insert,
                Parent = RequireString(e, "parent"),
                Index = (int)RequireLong(e, "index"),
                Node = ParseNode(RequireElement(e, "node")),
            },
            "remove" => new PatchOp
            {
                Kind = PatchOpKind.Remove,
                Id = RequireString(e, "id"),
            },
            "move" => new PatchOp
            {
                Kind = PatchOpKind.Move,
                Id = RequireString(e, "id"),
                Parent = RequireString(e, "parent"),
                Index = (int)RequireLong(e, "index"),
            },
            "window" => new PatchOp
            {
                Kind = PatchOpKind.Window,
                Props = RequireObject(e, "props"),
            },
            _ => throw new ProtocolException("bad-op", $"未知 patch 操作 \"{t}\""),
        };
    }

    // ---- 字段读取辅助 ----

    /// <summary>
    /// 把 JsonElement 转成脱离 JsonDocument 生命周期的独立 JsonObject。
    /// 直接持有 element 会在 using 结束后变成非法访问，所以必须复制。
    /// </summary>
    private static JsonObject ToJsonObject(JsonElement e) =>
        (JsonObject)JsonNode.Parse(e.GetRawText(), null, default)!;

    private static JsonElement RequireElement(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v))
            throw new ProtocolException("bad-protocol", $"缺少必需字段 \"{name}\"");
        return v;
    }

    private static string RequireString(JsonElement e, string name)
    {
        var v = RequireElement(e, name);
        if (v.ValueKind != JsonValueKind.String)
            throw new ProtocolException("bad-protocol", $"字段 \"{name}\" 应为字符串");
        return v.GetString()!;
    }

    private static long RequireLong(JsonElement e, string name)
    {
        var v = RequireElement(e, name);
        if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt64(out var n))
            throw new ProtocolException("bad-protocol", $"字段 \"{name}\" 应为整数");
        return n;
    }

    private static JsonObject RequireObject(JsonElement e, string name)
    {
        var v = RequireElement(e, name);
        if (v.ValueKind != JsonValueKind.Object)
            throw new ProtocolException("bad-protocol", $"字段 \"{name}\" 应为对象");
        return ToJsonObject(v);
    }

    private static JsonElement RequireArray(JsonElement e, string name)
    {
        var v = RequireElement(e, name);
        if (v.ValueKind != JsonValueKind.Array)
            throw new ProtocolException("bad-protocol", $"字段 \"{name}\" 应为数组");
        return v;
    }

    private static Dictionary<string, string>? TryEvents(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Object)
            return null;

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in v.EnumerateObject())
        {
            if (p.Value.ValueKind != JsonValueKind.String)
                throw new ProtocolException("bad-protocol", $"事件 \"{p.Name}\" 的 handler 必须是字符串");
            map[p.Name] = p.Value.GetString()!;
        }
        return map;
    }

    private static string? TryString(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? TryDouble(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static long? TryLong(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;

    private static bool? TryBool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean()
            : null;
}
