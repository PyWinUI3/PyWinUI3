"""扩展控件：补齐 WinUI 3 常用控件在 Python 侧的封装。

这些类和 :mod:`pywinui3.widgets` 里的基础控件遵循完全相同的规则：
只保存属性描述，赋值即发 patch，事件用 ``bind()`` 挂，运行时值用 ``query()`` 读。

命名与 C# 宿主 ``ExtendedControls`` 的 case 一一对应。少数控件因为
WinUI 自身的限制做了简化，见 README「已知限制」。
"""

from __future__ import annotations

from typing import Any, ClassVar, Iterable

from .widgets import Widget


class _ValueWidget(Widget):
    """带一个主值的控件共用的 ``value`` / ``get()`` / ``set()``。"""

    _value_key: ClassVar[str] = "value"
    _default: ClassVar[Any] = 0
    _var_prop: ClassVar[str | None] = "value"
    _var_field: ClassVar[str | None] = "value"

    @property
    def value(self) -> Any:
        return self._props.get(self._value_key, self._default)

    @value.setter
    def value(self, value: Any) -> None:
        self.configure(**{self._value_key: value})

    def get(self) -> Any:
        return self.value

    def set(self, value: Any) -> None:
        self.value = value


class _TextWidget(_ValueWidget):
    _value_key: ClassVar[str] = "text"
    _default: ClassVar[Any] = ""
    _var_prop: ClassVar[str | None] = "text"
    _var_field: ClassVar[str | None] = "text"


class _ItemsWidget(Widget):
    """带 ``items`` 列表的控件共用选中项读取。"""

    _var_prop: ClassVar[str | None] = "selectedIndex"
    _var_field: ClassVar[str | None] = "index"

    def __init__(self, master: Any, items: Iterable[Any] | None = None, **kwargs: Any):
        if items is not None:
            kwargs.setdefault("items", list(items))
        super().__init__(master, **kwargs)

    @property
    def selected_index(self) -> int:
        """本地记住的选中下标；用户操作后由 change 事件同步。"""
        return int(self._props.get("selectedIndex", -1))

    @selected_index.setter
    def selected_index(self, index: int) -> None:
        self.configure(selected_index=index)

    @property
    def selected_item(self) -> Any:
        """向宿主读当前选中项（真实状态，不依赖本地缓存）。"""
        return self.query("selectedItem")

    @property
    def selected_items(self) -> list[Any]:
        """多选模式下所有选中项。"""
        return list(self.query("selectedItems") or [])

    @property
    def item_count(self) -> int:
        return int(self.query("itemCount") or 0)

    def scroll_to(self, index: int = 0) -> None:
        self._app._send_invoke(self._id, "scroll_to", {"index": index})


# --------------------------------------------------------------------------
# 表单输入
# --------------------------------------------------------------------------


class NumberBox(_ValueWidget):
    """带上下微调按钮的数字输入框。

    ``value`` / ``min`` / ``max`` / ``step_frequency`` 都是数值；
    ``spin`` 可取 ``compact``（默认）/ ``inline`` / ``hidden``。
    """

    _type = "NumberBox"

    def __init__(
        self,
        master: Any,
        value: float = 0,
        *,
        min: float = -float("inf"),
        max: float = float("inf"),
        step_frequency: float = 1,
        **kwargs: Any,
    ):
        # ±inf 不能进 JSON，只在用户真的给了有限值时才下发
        props: dict[str, Any] = {"value": value, "step_frequency": step_frequency}
        if min != -float("inf"):
            props["min"] = min
        if max != float("inf"):
            props["max"] = max
        props.update(kwargs)
        super().__init__(master, **props)


class PasswordBox(_TextWidget):
    """密码输入框（比 ``Entry(password=True)`` 更直白的类型名）。"""

    _type = "PasswordBox"

    def __init__(self, master: Any, text: str = "", **kwargs: Any):
        super().__init__(master, text=text, **kwargs)


class Icon(Widget):
    """图标节点（Segoe Fluent Icons 码点）。

    ::

        ui.Icon(col, glyph="\\uE8A5", font_size=20, foreground="#0F6CBD")

    主要用途是放进 ``items.children`` 给列表项配图标。
    """

    _type = "Icon"

    def __init__(self, master: Any, glyph: str = "", **kwargs: Any):
        super().__init__(master, glyph=glyph, **kwargs)


class RichTextBlock(_TextWidget):
    """只读富文本块。

    两种写法：

    - ``text="纯文本"``：整段无格式
    - ``rich_text=[{"text": "粗", "bold": True}, ...]``：逐段设置格式，
      片段字段支持 ``text`` / ``bold`` / ``italic`` / ``underline`` /
      ``size`` / ``color``
    """

    _type = "RichTextBlock"

    def __init__(self, master: Any, text: str = "", **kwargs: Any):
        # 默认换行，但允许调用方覆盖（之前硬编码会导致 wrap= 重复传参报错）
        kwargs.setdefault("wrap", True)
        super().__init__(master, text=text, **kwargs)


class RichEditBox(_TextWidget):
    """多行可编辑文本框。"""

    _type = "RichEditBox"

    def __init__(self, master: Any, text: str = "", **kwargs: Any):
        kwargs.setdefault("height", 120)
        super().__init__(master, text=text, **kwargs)


class AutoSuggestBox(_TextWidget):
    """带建议列表的搜索框。

    ``items`` 是候选列表；``query_submitted`` / ``suggestion_chosen`` 事件
    分别对应回车提交和选中建议，``submit`` 也可以用。
    """

    _type = "AutoSuggestBox"

    def __init__(
        self,
        master: Any,
        text: str = "",
        *,
        items: Iterable[str] | None = None,
        **kwargs: Any,
    ):
        if items is not None:
            kwargs.setdefault("items", list(items))
        super().__init__(master, text=text, **kwargs)


class DatePicker(_ValueWidget):
    """日期选择。``date`` 用 ``YYYY-MM-DD`` 字符串。"""

    _type = "DatePicker"
    _value_key = "date"
    _default = ""
    _var_prop = "date"
    _var_field = "date"

    def __init__(self, master: Any, date: str = "", **kwargs: Any):
        super().__init__(master, date=date, **kwargs)


class TimePicker(_ValueWidget):
    """时间选择。``time`` 用 ``HH:MM`` 字符串。"""

    _type = "TimePicker"
    _value_key = "time"
    _default = ""
    _var_prop = "time"
    _var_field = "time"

    def __init__(self, master: Any, time: str = "12:00", **kwargs: Any):
        super().__init__(master, time=time, **kwargs)


class CalendarDatePicker(DatePicker):
    """下拉日历式的日期选择。"""

    _type = "CalendarDatePicker"


class CalendarView(DatePicker):
    """整月日历。``selection_mode`` 可取 ``single`` / ``multiple`` / ``none``。"""

    _type = "CalendarView"

    def __init__(self, master: Any, date: str = "", **kwargs: Any):
        kwargs.setdefault("height", 320)
        super().__init__(master, date=date, **kwargs)


class RatingControl(_ValueWidget):
    """星级评分，``value`` 默认 0，``max_rating`` 默认 5。"""

    _type = "RatingControl"

    def __init__(self, master: Any, value: float = 0, **kwargs: Any):
        super().__init__(master, value=value, **kwargs)


class ColorPicker(_ValueWidget):
    """颜色选择器。``color`` 用 ``#AARRGGBB`` 或 ``#RRGGBB``。"""

    _type = "ColorPicker"
    _value_key = "color"
    _default = "#FFFFFFFF"
    _var_prop = "color"
    _var_field = "color"

    def __init__(self, master: Any, color: str = "#FFFFFFFF", **kwargs: Any):
        super().__init__(master, color=color, **kwargs)


class DropDownButton(Widget):
    """点击弹出菜单的按钮；菜单项用 ``set_menu()`` 设置，点击回 ``menu`` 事件。"""

    _type = "DropDownButton"

    def __init__(self, master: Any, text: str = "选择", **kwargs: Any):
        super().__init__(master, text=text, **kwargs)


class ToggleSplitButton(Widget):
    """带开关状态的分段按钮。"""

    _type = "ToggleSplitButton"
    _var_prop = "checked"
    _var_field = "checked"

    def __init__(self, master: Any, text: str = "选项", checked: bool = False, **kwargs: Any):
        super().__init__(master, text=text, checked=checked, **kwargs)

    @property
    def value(self) -> bool:
        return bool(self._props.get("checked", False))

    @value.setter
    def value(self, checked: bool) -> None:
        self.configure(checked=checked)


class PersonPicture(Widget):
    """头像。``display_name`` 显示姓名，``initials`` 直接指定首字母。"""

    _type = "PersonPicture"

    def __init__(self, master: Any, display_name: str = "", **kwargs: Any):
        super().__init__(master, display_name=display_name, **kwargs)


# --------------------------------------------------------------------------
# 数据 / 导航
# --------------------------------------------------------------------------


class GridView(_ItemsWidget):
    """网格列表。``selection_mode`` 支持 ``single`` / ``multiple`` / ``extended``。"""

    _type = "GridView"

    def __init__(self, master: Any, items: Iterable[Any] | None = None, **kwargs: Any):
        kwargs.setdefault("height", 200)
        super().__init__(master, items, **kwargs)


class FlipView(_ItemsWidget):
    """一次显示一项、可左右翻页的列表。"""

    _type = "FlipView"

    def __init__(self, master: Any, items: Iterable[Any] | None = None, **kwargs: Any):
        kwargs.setdefault("height", 200)
        super().__init__(master, items, **kwargs)


class Pivot(_ItemsWidget):
    """顶部标签切换。"""

    _type = "Pivot"

    def __init__(self, master: Any, items: Iterable[Any] | None = None, **kwargs: Any):
        kwargs.setdefault("height", 200)
        super().__init__(master, items, **kwargs)


class TabView(_ItemsWidget):
    """标签页容器。

    两类子元素都是真正的标签页（宿主里都是 ``TabViewItem``），区别只在行为：

    - :class:`PinnedTab` —— 固定选项卡，**不可关闭、不可拖动重排**，永远排在
      标签条最前面；同样能放子控件、同样支持 ``icon``。
    - :class:`TabPage` —— 普通标签页，**可拖动重排、可关闭**，也能放子控件。

    ``items=[...]`` 仍是快捷写法，生成一组可关闭的普通标签页（不能放子控件）。
    用 ``PinnedTab`` / ``TabPage`` 子节点时不要再传 ``items``，两者会同时出现在
    标签条上。

    关闭按钮点击后宿主回 ``tabCloseRequested``，容器默认销毁对应 ``TabPage``
    （走 :meth:`Widget.destroy`）；用户仍可 ``bind("tabCloseRequested", fn)``
    收到通知，两种逻辑互不抢占。
    """

    _type = "TabView"

    def __init__(self, master: Any, items: Iterable[Any] | None = None, **kwargs: Any):
        kwargs.setdefault("height", 200)
        kwargs.setdefault("can_reorder_tabs", True)
        super().__init__(master, items, **kwargs)

    def _on_event(self, event: Any) -> None:
        # 内部钩子先于用户回调执行，且不会被 bind() 覆盖（见 App._dispatch_event）。
        # 用户点关闭时把对应节点从 Python 侧控件树里摘掉，否则宿主移除了标签、
        # Python 还留着一个孤儿节点，后续 set/query 都会报 unknown-node。
        if event.name != "tabCloseRequested":
            return
        node_id = event.data.get("nodeId")
        if not isinstance(node_id, str):
            value = event.data.get("value")
            for child in list(self._children):
                if getattr(child, "text", None) == value:
                    node_id = child._id
                    break
        if isinstance(node_id, str):
            target = self._app._nodes.get(node_id)
            if target is not None:
                target.destroy()

    def add_page(self, text: str, **kwargs: Any) -> "TabPage":
        """追加一个可移动、可关闭的标签页，返回可放子控件的容器。"""
        return TabPage(self, text, **kwargs)

    def add_pinned(self, text: str, **kwargs: Any) -> "PinnedTab":
        """追加一个不可移动、不可关闭的固定选项卡（同样是标签页）。"""
        return PinnedTab(self, text, **kwargs)


class PinnedTab(Widget):
    """固定选项卡：真正的标签页，只是不能关闭、不能拖动重排。

    宿主里它和 :class:`TabPage` 一样是 ``TabViewItem``，区别只有两点：
    ``IsClosable=false``，以及拖动会被取消；用户把普通标签拖到它前面时，
    拖动结束后固定标签会整块归位到最前面。

    因此它同样能放子控件、同样支持 ``icon``（Segoe 字形或本地图片路径），
    选中时同样回 ``tabSelected`` 事件，``data`` 带 ``value`` / ``nodeId``。

    ``min_width`` / ``max_width`` 直接约束标签宽度；标题超宽时自动用 ``…``
    省略，读回 ``text`` 仍是完整原文。
    """

    _type = "PinnedTab"

    def __init__(self, master: Any, text: str = "", **kwargs: Any):
        kwargs.setdefault("text", text)
        super().__init__(master, **kwargs)

    def select(self) -> None:
        """把这个固定选项卡切到前台（和点它一下等价）。"""
        self._app._send_invoke(self._id, "select_tab")


class TabPage(Widget):
    """普通标签页：可拖动重排、可关闭，并且能放子控件。

    ``closable=False`` 可关掉关闭按钮；``icon`` 支持 Segoe 字形（如
    ``"\\uE80F"``）或本地图片路径（``.png`` / ``.jpg`` / ``.ico`` / ``.bmp`` /
    ``.svg``）。SVG 由宿主用 ``SvgImageSource`` 栅格化，高 DPI 下也清晰。

    ``min_width`` / ``max_width`` 直接约束标签宽度；标题超宽时自动用 ``…``
    省略，读回 ``text`` 仍是完整原文。

    子控件直接写在下面即可，宿主会在 ``TabViewItem.Content`` 里预置一层
    ``StackPanel``，挂载规则和 :class:`Column` 完全一致。
    """

    _type = "TabPage"

    def __init__(self, master: Any, text: str = "", **kwargs: Any):
        kwargs.setdefault("text", text)
        kwargs.setdefault("closable", True)
        super().__init__(master, **kwargs)

    def close(self) -> None:
        """程序化关闭：等价于用户点关闭按钮。"""
        self.destroy()

    def select(self) -> None:
        """把这个标签页切到前台。"""
        self._app._send_invoke(self._id, "select_tab")


class TreeView(_ItemsWidget):
    """树。``items`` 里可以放 ``{"label": "父", "children": [...]}`` 表示层级。"""

    _type = "TreeView"

    def __init__(self, master: Any, items: Iterable[Any] | None = None, **kwargs: Any):
        kwargs.setdefault("height", 220)
        super().__init__(master, items, **kwargs)


class NavigationView(_ItemsWidget):
    """WinUI 原生导航侧栏（和用 ListBox 模拟的自动收缩侧栏不是一回事）。

    ``pane_display_mode`` 可取 ``left`` / ``top`` / ``compact`` / ``minimal``。
    子控件会放进右侧内容区。
    """

    _type = "NavigationView"

    def __init__(self, master: Any, items: Iterable[Any] | None = None, **kwargs: Any):
        kwargs.setdefault("height", 320)
        super().__init__(master, items, **kwargs)


class MenuBar(_ItemsWidget):
    """顶部菜单栏，点击项回 ``click`` 事件，``e.data["index"]`` 是下标。"""

    _type = "MenuBar"

    def __init__(self, master: Any, items: Iterable[Any] | None = None, **kwargs: Any):
        super().__init__(master, items, **kwargs)


class CommandBar(_ItemsWidget):
    """命令栏，点击回 ``click`` 事件。"""

    _type = "CommandBar"

    def __init__(self, master: Any, items: Iterable[Any] | None = None, **kwargs: Any):
        super().__init__(master, items, **kwargs)


class BreadcrumbBar(_ItemsWidget):
    """面包屑导航，点击回 ``itemClick`` 事件。"""

    _type = "BreadcrumbBar"

    def __init__(self, master: Any, items: Iterable[Any] | None = None, **kwargs: Any):
        super().__init__(master, items, **kwargs)


class ItemsView(_ItemsWidget):
    """WinUI 1.8 的现代化集合视图（列表/网格统一控件）。"""

    _type = "ItemsView"

    def __init__(self, master: Any, items: Iterable[Any] | None = None, **kwargs: Any):
        kwargs.setdefault("height", 200)
        super().__init__(master, items, **kwargs)


class RadioButtons(_ItemsWidget):
    """成组单选按钮。"""

    _type = "RadioButtons"


class SelectorBar(_ItemsWidget):
    """WinUI 1.8 的选择条。"""

    _type = "SelectorBar"


# --------------------------------------------------------------------------
# 信息 / 容器
# --------------------------------------------------------------------------


class InfoBar(Widget):
    """应用内提示条。``severity`` 可取 ``info`` / ``success`` / ``warning`` / ``error``。"""

    _type = "InfoBar"

    def __init__(
        self,
        master: Any,
        title: str = "",
        message: str = "",
        *,
        severity: str = "info",
        **kwargs: Any,
    ):
        super().__init__(master, title=title, message=message, severity=severity, **kwargs)

    def show(self) -> None:
        self._app._send_invoke(self._id, "show")

    def hide(self) -> None:
        self._app._send_invoke(self._id, "hide")


class Expander(Widget):
    """可折叠容器，子控件放在 Content 里。"""

    _type = "Expander"
    _var_prop = "isExpanded"
    _var_field = "expanded"

    def __init__(self, master: Any, header: str = "", is_expanded: bool = False, **kwargs: Any):
        super().__init__(master, header=header, is_expanded=is_expanded, **kwargs)

    @property
    def value(self) -> bool:
        return bool(self._props.get("isExpanded", False))

    @value.setter
    def value(self, expanded: bool) -> None:
        self.configure(is_expanded=expanded)

    def show(self) -> None:
        self._app._send_invoke(self._id, "show")

    def hide(self) -> None:
        self._app._send_invoke(self._id, "hide")


class BorderBox(Widget):
    """带背景/边框的通用容器，可以放子控件。"""

    _type = "BorderBox"


class Viewbox(Widget):
    """把子内容等比缩放填满可用空间。"""

    _type = "Viewbox"


class RelativePanel(Widget):
    """相对布局面板。当前协议只开放其容器能力，未暴露附加对齐属性。"""

    _type = "RelativePanel"

    def __init__(self, master: Any, **kwargs: Any):
        kwargs.setdefault("height", 160)
        super().__init__(master, **kwargs)


class TwoPaneView(Widget):
    """双栏容器，子控件进 Pane1；``pane_priority`` 可切到 Pane2。"""

    _type = "TwoPaneView"

    def __init__(self, master: Any, **kwargs: Any):
        kwargs.setdefault("height", 220)
        super().__init__(master, **kwargs)


class RefreshContainer(Widget):
    """下拉刷新容器，触发 ``refreshRequested`` 事件。"""

    _type = "RefreshContainer"

    def __init__(self, master: Any, **kwargs: Any):
        kwargs.setdefault("height", 200)
        super().__init__(master, **kwargs)


class Canvas(Widget):
    """绝对定位画布：子控件用 ``left`` / ``top`` 定位，超出边界不裁切。

    ``view`` 控制整体缩放与平移，一条属性搞定，开销与图元数量无关::

        canvas = ui.Canvas(win, width=800, height=600)
        ui.Rect(canvas, left=20, top=30, width=100, height=60, fill="#0F6CBD")
        canvas.view = {"scale": 1.5, "offsetX": 40, "offsetY": -20}
    """

    _type = "Canvas"

    def __init__(self, master: Any, *, view: dict[str, Any] | None = None, **kwargs: Any):
        if view is not None:
            kwargs["view"] = view
        super().__init__(master, **kwargs)


class _Shape(Widget):
    """图元基类：统一 ``fill`` / ``stroke`` / ``stroke_width`` 入口。

    三个参数都只在非 None 时下发，避免把宿主默认值覆盖成空。
    """

    def __init__(
        self,
        master: Any,
        *,
        fill: str | None = None,
        stroke: str | None = None,
        stroke_width: float | None = None,
        **kwargs: Any,
    ):
        if fill is not None:
            kwargs["fill"] = fill
        if stroke is not None:
            kwargs["stroke"] = stroke
        if stroke_width is not None:
            kwargs["stroke_width"] = stroke_width
        super().__init__(master, **kwargs)


class Rect(_Shape):
    """矩形。``left`` / ``top`` 定位，``radius_x`` / ``radius_y`` 圆角。"""

    _type = "Rect"


class Ellipse(_Shape):
    """椭圆，尺寸由 ``width`` / ``height`` 决定。"""

    _type = "Ellipse"


class Line(_Shape):
    """直线：``x1, y1`` → ``x2, y2``。"""

    _type = "Line"

    def __init__(self, master: Any, x1: float = 0, y1: float = 0, x2: float = 0, y2: float = 0, **kwargs: Any):
        kwargs.update({"x1": x1, "y1": y1, "x2": x2, "y2": y2})
        super().__init__(master, **kwargs)


class Path(_Shape):
    """路径，``d`` 用 SVG 路径语法（M / L / C / Q / A / Z …）。

    XAML 的路径标记与 SVG 基本互通，可以直接把 SVG 里的 d 传进来。
    """

    _type = "Path"

    def __init__(self, master: Any, d: str = "", **kwargs: Any):
        if d:
            kwargs["d"] = d
        super().__init__(master, **kwargs)


class Polygon(_Shape):
    """闭合多边形，``points`` 用 ``[[x, y], ...]``。"""

    _type = "Polygon"

    def __init__(self, master: Any, points: list[list[float]] | None = None, **kwargs: Any):
        if points is not None:
            kwargs["points"] = points
        super().__init__(master, **kwargs)


class Polyline(Polygon):
    """折线（不闭合），与 Polygon 同构。"""

    _type = "Polyline"


__all__ = [
    "NumberBox",
    "PasswordBox",
    "Icon",
    "RichTextBlock",
    "RichEditBox",
    "AutoSuggestBox",
    "DatePicker",
    "TimePicker",
    "CalendarDatePicker",
    "CalendarView",
    "RatingControl",
    "ColorPicker",
    "DropDownButton",
    "ToggleSplitButton",
    "PersonPicture",
    "GridView",
    "FlipView",
    "Pivot",
    "TabView",
    "PinnedTab",
    "TabPage",
    "TreeView",
    "NavigationView",
    "MenuBar",
    "CommandBar",
    "BreadcrumbBar",
    "ItemsView",
    "RadioButtons",
    "SelectorBar",
    "InfoBar",
    "Expander",
    "BorderBox",
    "Viewbox",
    "RelativePanel",
    "TwoPaneView",
    "RefreshContainer",
    "Canvas",
    "Rect",
    "Ellipse",
    "Line",
    "Path",
    "Polygon",
    "Polyline",
]
