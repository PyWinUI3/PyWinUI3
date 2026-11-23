using System.Text.Json.Nodes;

namespace PyWinUI.Host.Protocol;

/// <summary>
/// 窗口属性。全部可空：缺省值由 Rendering 层决定，协议层不预设默认，
/// 这样调整默认值不需要动协议。
/// </summary>
public sealed class WindowSpec
{
    public string? Title { get; set; }
    public double? Width { get; set; }
    public double? Height { get; set; }
    public double? MinWidth { get; set; }
    public double? MinHeight { get; set; }
    public bool? Resizable { get; set; }
    public bool? Center { get; set; }

    /// <summary>左上角位置（逻辑像素）。给了就覆盖 center。</summary>
    public double? X { get; set; }

    public double? Y { get; set; }

    /// <summary>窗口图标路径（.ico / .png），相对路径按进程工作目录解析。</summary>
    public string? Icon { get; set; }

    public bool? AlwaysOnTop { get; set; }

    /// <summary>
    /// 是否把「最大化」交给 Python 回调处理。
    ///
    /// false（默认）时标题栏上的最大化按钮是**禁用**的——宿主不自己决定
    /// 要不要最大化，只有 Python 那边挂了回调才启用。
    /// </summary>
    public bool? MaximizeRequestable { get; set; }
    public bool? Minimizable { get; set; }
    public bool? Maximizable { get; set; }

    /// <summary>启动时就最大化。</summary>
    public bool? Maximized { get; set; }

    /// <summary>
    /// 窗口内容是否可滚动。缺省视为 true：
    /// 内容比窗口高时必须能滚，否则等于没法用。
    /// </summary>
    public bool? Scroll { get; set; }

    /// <summary>system | light | dark</summary>
    public string? Theme { get; set; }

    /// <summary>auto | mica | acrylic | none</summary>
    public string? Backdrop { get; set; }

    /// <summary>
    /// 父窗口的会话 id。非空时本窗口以 WS_CHILD 嵌进父窗口客户区，
    /// 不再是独立顶层窗口——父窗口移动/最小化时它跟着走，父窗口关闭时一并关闭。
    /// </summary>
    public string? Parent { get; set; }

    /// <summary>
    /// 嵌入子窗口的 z 序层级：highest / lowest，缺省 highest。
    ///
    /// 只有这两个值：子 HWND 的 SetWindowPos 只认 HWND_TOP 和 HWND_BOTTOM，
    /// TOPMOST / NOTOPMOST 对 WS_CHILD 窗口无效，给别的值没有意义。
    /// </summary>
    public string? Layer { get; set; }
}

/// <summary>
/// 一个控件节点的声明。整棵树是扁平列表，父子关系由 <see cref="Children"/> 表达。
/// </summary>
public sealed class NodeSpec
{
    public string Id { get; set; } = "";

    /// <summary>控件类型，如 Label / Button / Column，见 protocol.md 3.3。</summary>
    public string Type { get; set; } = "";

    /// <summary>属性字典。保持为 JsonObject 以便按需惰性取值。</summary>
    public JsonObject Props { get; set; } = new();

    /// <summary>子节点 id，顺序即渲染顺序。</summary>
    public List<string>? Children { get; set; }

    /// <summary>事件名 → handler id。</summary>
    public Dictionary<string, string>? Events { get; set; }
}

public enum PatchOpKind
{
    /// <summary>设置已有节点的属性。</summary>
    Set,

    /// <summary>插入新节点。</summary>
    Insert,

    /// <summary>移除节点及其子树。</summary>
    Remove,

    /// <summary>把已有节点移到新的父节点与位置。</summary>
    Move,

    /// <summary>更新窗口属性。</summary>
    Window,
}

/// <summary>
/// 一条增量操作。
/// 解析阶段就已经决定了 <see cref="Kind"/> 并填好对应字段，
/// 应用阶段不需要再反复读 JsonElement。
/// </summary>
public sealed class PatchOp
{
    public PatchOpKind Kind { get; init; }

    /// <summary>Set / Remove / Move 的目标节点 id。</summary>
    public string? Id { get; init; }

    /// <summary>Insert / Move 的父节点 id。</summary>
    public string? Parent { get; init; }

    /// <summary>Insert / Move 的插入位置。</summary>
    public int Index { get; init; }

    /// <summary>Set / Window 的属性。</summary>
    public JsonObject? Props { get; init; }

    /// <summary>
    /// Set 操作可选携带：整组替换该节点的事件绑定。
    /// 非 null 时（哪怕是空字典）覆盖原有映射。
    /// </summary>
    public Dictionary<string, string>? Events { get; init; }

    /// <summary>Insert 的新节点。</summary>
    public NodeSpec? Node { get; init; }
}
