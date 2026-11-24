"""窗口。

``Window`` 同时承担两个角色：它是窗口（标题、尺寸、主题），
也是控件树的根（协议里的 ``root``）。这两件事共用同一个节点 id，
所以窗口内容根不需要额外包一层容器——``Column`` 的堆叠语义直接生效。
"""

from __future__ import annotations

from typing import Any, Callable

from . import protocol
from .app import App
from .widgets import Widget, camelize, normalize_value


#: 标题栏两个新属性的别名。早期示例把 ReturnButton 写成了 RetrunButtom，
#: 这里一并收下，避免用户照着旧代码写时静默失效。
_TITLE_BAR_KEYS = {
    "returnbutton": "returnButton",
    "retrunbuttom": "returnButton",
    "textinputbox": "textInputBox",
}


def _window_key(name: str) -> str:
    """窗口属性名 → 协议里的驼峰名。"""
    return _TITLE_BAR_KEYS.get(name.replace("_", "").lower(), camelize(name))


class Window(Widget):
    """顶层窗口。

    ``layout`` 决定窗口内容根本身是什么容器：

    ==========  ================================  ==============================
    值            根节点                            适合
    ==========  ================================  ==============================
    ``column``   ``StackPanel``（默认，垂直堆叠）      普通表单
    ``row``      ``StackPanel``（水平堆叠）           横向排布
    ``grid``     ``Grid``                          侧栏 + 内容区这类整屏布局
    ==========  ================================  ==============================

    只有 ``layout="grid"`` 时，窗口根节点才会被当成 Grid：
    子控件的 ``.grid(row=, column=)`` 直接相对窗口定位，``*`` 行列也能拿到
    真实的剩余高度——侧边栏要撑满整窗高度就靠这个。

    ::

        win = ui.Window(title="工具", layout="grid", scroll=False,
                        rows=["*"], columns=["auto", "*"])
        side = ui.ListBox(win, width=220, auto_collapse=True, collapsed_width=40)
        side.grid(row=0, column=0)

    嵌入子窗口：传 ``parent`` 后本窗口不再是独立顶层窗口，
    而是以 WS_CHILD 嵌进父窗口的客户区。::

        main = ui.Window(title="主窗口")
        sub = ui.Window(app, title="子窗口", parent=main,
                        width=480, height=320, layer="highest")
        sub.render()

    ``layer`` 只有两档，正好对应子 HWND 唯一有效的两个 z 序位置：
    ``highest``（``HWND_TOP``，压在同父的其他子窗口之上）和
    ``lowest``（``HWND_BOTTOM``）。``TOPMOST`` / ``NOTOPMOST`` 对 ``WS_CHILD``
    窗口无效，所以不开放。

    嵌入后的行为：保留系统标题栏（标题文字、按住拖动、关闭按钮都可用，
    但没有最小化/最大化——子窗口缩不到任务栏去）、跟随父窗口移动、
    父窗口缩放时按比例跟随、父窗口关闭时一并关闭。
    子窗口占父客户区的比例由 ``width`` / ``height`` / ``x`` / ``y`` 换算得出。
    """

    #: 窗口内容根就是垂直容器，所以直接子控件默认从上往下排列。
    _type = "Column"

    #: 这些属性属于窗口本身，走 ``window`` 操作；其余走节点 ``set``。
    _WINDOW_ATTRS = frozenset(
        {
            "title",
            "width",
            "height",
            "min_width",
            "min_height",
            "resizable",
            "center",
            "x",
            "y",
            "icon",
            "always_on_top",
            "minimizable",
            "maximizable",
            "maximize_requestable",
            "return_button",
            "retrun_buttom",
            "returnButton",
            "RetrunButtom",
            "text_input_box",
            "textInputBox",
            "TextInputBox",
            "maximized",
            "scroll",
            "theme",
            "backdrop",
        }
    )

    #: 窗口根节点可用的布局类型。默认 Column：直接子控件从上往下排。
    _LAYOUTS = {"column": "Column", "row": "Row", "grid": "Grid"}

    #: 嵌入子窗口的层级。子 HWND 调 SetWindowPos 只认 HWND_TOP / HWND_BOTTOM，
    #: TOPMOST / NOTOPMOST 对 WS_CHILD 窗口无效，所以只开放这两种。
    _LAYERS = frozenset({"highest", "lowest"})

    def __init__(
        self,
        app: App | None = None,
        *,
        title: str = "PyWinUI3",
        width: int = 800,
        height: int = 600,
        scroll: bool = True,
        layout: str = "column",
        session: str | None = None,
        parent: "Window | None" = None,
        layer: str = "highest",
        **kwargs: Any,
    ):
        # 不走 super().__init__：Window 没有 master，初始化路径与普通控件不同
        self._app = app if app is not None else App.default()

        # 嵌入层级：子窗口的 z 序只有"最上/最下"两档
        if layer not in self._LAYERS:
            raise ValueError(
                f"layer 只能是 highest 或 lowest，收到 {layer!r}。"
                "子窗口的 z 序只对应 HWND_TOP / HWND_BOTTOM，TOPMOST 那类值对 WS_CHILD 无效。"
            )
        if parent is self:
            raise ValueError("parent 不能指向窗口自己")

        # session=None 时：第一扇窗是 main，后续自动分配 w1/w2...
        if session is None:
            session = "main" if not self._app._windows else self._app._alloc_session_id()
        self._session = session
        self._type = self._LAYOUTS.get(layout, "Column")
        self._id = self._app._alloc_node_id()
        self._parent = None
        self._children = []
        # spacing 只有堆叠容器认；放到 Grid 上会变成一条「未知属性」告警
        self._props: dict[str, Any] = {"spacing": 0} if self._type in ("Column", "Row") else {}
        self._events: dict[str, Callable[..., Any]] = {}
        self._handler_ids: dict[str, str] = {}
        self._destroyed = False
        # Window 不绑定变量，但事件分发会统一调用 _sync_from_event，需要这个占位
        self._variable: Any = None
        # 关闭按钮的接管回回调；None 表示默认行为（直接关）
        self._close_request_handler: Callable[..., Any] | None = None
        self._maximize_request_handler: Callable[..., Any] | None = None
        self._return_request_handler: Callable[..., Any] | None = None
        self._text_input_handler: Callable[..., Any] | None = None
        self._text_changed_handler: Callable[..., Any] | None = None

        self._window_props: dict[str, Any] = {
            "title": title,
            "width": width,
            "height": height,
            "scroll": scroll,
        }

        # 嵌入子窗口：只登记父会话 id 和层级，几何由宿主按父客户区比例算
        if parent is not None:
            self._window_props["parent"] = parent._session
            self._window_props["layer"] = layer

        # 窗口专属属性从 kwargs 里摘出来，剩下的当成容器属性
        for key in list(kwargs):
            if key in self._WINDOW_ATTRS:
                self._window_props[_window_key(key)] = normalize_value(kwargs.pop(key))

        if kwargs:
            from .widgets import build_props

            self._props.update(build_props(kwargs))

        # Grid 根节点默认给一个 "*" 行，否则没有行定义时子控件会按内容高度排
        if self._type == "Grid" and "rows" not in self._props:
            self._props["rows"] = ["*"]

        self._app._register_node(self)
        self._app._attach_window(self)

    # ---- 窗口属性 ----

    def _check_title_bar_handlers(self, props: dict[str, Any]) -> None:
        """两个扩展部件必须挂回调。

        宿主只发事件、不代替用户做决定；没挂回调的话按钮点了没反应、
        输入框打了字没人收，等于一个坏控件。构造时不查（用户要等构造完
        才能挂回调），在 render 和运行期 configure 开启时查。
        """
        if props.get("returnButton") and self._return_request_handler is None:
            raise ValueError(
                "return_button=True 必须先调用 win.on_return_request(callback) 挂接回调；"
                "宿主不会自己处理返回动作"
            )
        if props.get("textInputBox") and not (
            self._text_input_handler or self._text_changed_handler
        ):
            raise ValueError(
                "text_input_box=True 必须先调用 win.on_text_input(callback) "
                "或 win.on_text_changed(callback) 挂接回调；"
                "宿主不会自己接收输入"
            )

    def _window_spec(self) -> dict[str, Any]:
        props = dict(self._window_props)
        self._check_title_bar_handlers(props)
        return props

    def configure(self, **kwargs: Any) -> None:
        # 父子关系是建窗时定死的：子 HWND 已经 SetParent 到父窗口上，
        # 运行期换父需要销毁重建，超出 configure 的语义。
        for fixed in ("parent", "layer"):
            if fixed in kwargs:
                raise ValueError(f"{fixed} 只能在构造 Window 时指定，运行期不支持修改")

        window_props: dict[str, Any] = {}
        node_props: dict[str, Any] = {}

        for key, value in kwargs.items():
            if key in self._WINDOW_ATTRS:
                window_props[_window_key(key)] = normalize_value(value)
            else:
                node_props[camelize(key)] = normalize_value(value)

        if window_props:
            # 运行期开启扩展部件时同样要求已挂回调（关闭不查）
            merged = {**self._window_props, **window_props}
            self._check_title_bar_handlers(merged)
            self._window_props.update(window_props)
            self._app._send_patch(
                [protocol.op_window(window_props, session=self._session)]
            )

        if node_props:
            self._props.update(node_props)
            self._app._send_patch([protocol.op_set(self._id, node_props)])

    config = configure

    def __getattr__(self, name: str) -> Any:
        if name.startswith("_"):
            raise AttributeError(name)
        key = _window_key(name)
        window_props = self.__dict__.get("_window_props", {})
        if key in window_props:
            return window_props[key]
        return super().__getattr__(name)

    # ---- 生命周期 ----

    def mainloop(self) -> None:
        """起宿主、渲染、进入事件循环。"""
        self._app.mainloop()

    # ---- 窗口级控制 ----

    def hide(self) -> None:
        """隐藏窗口但保持会话，之后可用 :meth:`show` 恢复。"""
        self._app._window_action(self._session, "hide")

    def show(self) -> None:
        """显示并激活一扇之前隐藏的窗口。"""
        self._app._window_action(self._session, "show")

    def focus(self) -> None:
        """把窗口激活到前台。"""
        self._app._window_action(self._session, "focus")

    def close(self) -> None:
        """关闭这扇窗口；若是 main 窗口则退出整个应用。"""
        self._app._window_action(self._session, "close")

    def render(self) -> None:
        """把这扇窗口的控件树发给宿主。多窗口时每扇窗都要调一次。"""
        self._app._render(self)

    def quit(self) -> None:
        self._app.quit()

    def destroy(self) -> None:
        self._app.quit()

    def after(self, ms: int, callback: Callable[[], None]) -> None:
        self._app.after(ms, callback)

    def on_close(self, callback: Callable[..., None]) -> None:
        """窗口即将关闭时回调。首版不支持取消关闭。"""
        self.bind("closing", callback)

    def on_close_request(self, callback: Callable[..., None] | None) -> None:
        """接管标题栏上的关闭按钮。

        嵌入子窗口的关闭按钮**不会**直接关窗，宿主会先发一条
        ``closeRequested`` 事件回来。给了回调就由你决定怎么处理；
        不注册（或传 None）则默认直接关闭这扇窗口::

            def ask(_e):
                if ui.ask_yes_no("确定关闭？"):
                    sub.close()

            sub.on_close_request(ask)

        这和 :meth:`on_close` 不同：``on_close`` 是窗口已经在关的路上，
        拿不到取消机会；这里窗口还开着，关不关完全由你决定。
        """
        self._close_request_handler = callback

    def on_maximize_request(self, callback: Callable[..., None] | None) -> None:
        """接管标题栏上的最大化 / 还原按钮。

        宿主**不会**自己做最大化——它把 ``maximizeRequested`` 事件发回来，
        由你决定要做什么。没注册回调（或者传 None）时，标题栏上的最大化
        按钮是**禁用**的（灰着，点不动）::

            def on_max(e):
                if e.data.get("maximized"):
                    w.restore()
                else:
                    w.maximize()

            w.on_maximize_request(on_max)

        ``e.data["maximized"]`` 是当前是不是最大化状态。
        """
        self._maximize_request_handler = callback

        # 告诉宿主按钮该不该启用：没回调就禁用
        requestable = callback is not None
        self._window_props["maximizeRequestable"] = requestable

        if self._app is not None and self._app._host is not None:
            self._app._send_patch(
                [protocol.op_window(
                    {"maximizeRequestable": requestable}, session=self._session
                )]
            )

    def on_return_request(self, callback: Callable[..., None] | None) -> None:
        """接管标题栏应用图标左边的返回按钮。

        宿主自己不会动窗口，只发 ``returnRequested``，由你决定返回到哪::

            w.on_return_request(lambda e: w.close())

        按钮要先用 ``return_button=True`` 打开。传 None 会**立刻隐藏**按钮
        （等价于 ``configure(return_button=False)``），不会留一个点了没反应的空按钮。
        """
        self._return_request_handler = callback
        self._sync_title_bar("returnButton", callback is not None)

    def on_text_input(self, callback: Callable[..., None] | None) -> None:
        """标题栏输入框里按下回车时回调，``e.data["text"]`` 是当前文本。

        输入框要先用 ``text_input_box=True`` 打开::

            def submit(e):
                print("搜索:", e.data.get("text", ""))

            w.on_text_input(submit)
        """
        self._text_input_handler = callback
        # 两个输入回调都没了才隐藏输入框——只撤一个不算
        self._sync_title_bar(
            "textInputBox",
            callback is not None or self._text_changed_handler is not None,
        )

    def on_text_changed(self, callback: Callable[..., None] | None) -> None:
        """标题栏输入框的内容每次变化都回调，``e.data["text"]`` 是新文本。"""
        self._text_changed_handler = callback
        self._sync_title_bar(
            "textInputBox",
            callback is not None or self._text_input_handler is not None,
        )

    def _sync_title_bar(self, key: str, has_handler: bool) -> None:
        """撤掉最后一个回调时自动隐藏部件。

        只管「关」不管「开」：挂回调只是登记处理函数，不该反过来把
        用户没开的部件自动显示出来。开启仍由 return_button/text_input_box
        属性显式控制。
        """
        if has_handler or not self._window_props.get(key):
            return

        self._window_props[key] = False
        # 必须立刻 flush：configure() 走 16ms 合并窗口，
        # 紧接着的 render()/事件循环会让隐藏晚一帧，看起来像没生效。
        if self._app is not None and self._app._host is not None:
            self._app._send_patch(
                [protocol.op_window({key: False}, session=self._session)]
            )
            self._app._flush_patches()

    def _on_event(self, event: Any) -> None:
        # 自绘标题栏的关闭按钮走这里，而不是用户 bind 的事件通道
        if getattr(event, "name", None) == "closeRequested":
            handler = self._close_request_handler
            if handler is None:
                self.close()
            else:
                handler(event)
            return

        if getattr(event, "name", None) == "maximizeRequested":
            handler = self._maximize_request_handler
            if handler is not None:
                handler(event)
            return

        if getattr(event, "name", None) == "returnRequested":
            handler = self._return_request_handler
            if handler is not None:
                handler(event)
            return

        if getattr(event, "name", None) == "textInputSubmitted":
            handler = self._text_input_handler
            if handler is not None:
                handler(event)
            return

        if getattr(event, "name", None) == "textInputChanged":
            handler = self._text_changed_handler
            if handler is not None:
                handler(event)
            return

        super()._on_event(event)

    def on_resize(self, callback: Callable[..., None]) -> None:
        """窗口尺寸变化时回调，``e.data`` 里是 ``width`` / ``height``（逻辑像素）。"""
        self.bind("sizeChanged", callback)

    def on_activate(self, callback: Callable[..., None]) -> None:
        """窗口获得/失去焦点时回调，``e.data["active"]`` 是布尔值。"""
        self.bind("windowActivated", callback)

    def on_state_changed(self, callback: Callable[..., None]) -> None:
        """最大化 / 最小化 / 还原时回调，``e.data["state"]`` 是窗口状态。"""
        self.bind("windowStateChanged", callback)

    # ---- 运行时状态（query/result 读回） ----

    @property
    def state(self) -> str:
        """``normal`` / ``maximized`` / ``minimized``。"""
        return str(self.query("windowState") or "normal")

    @property
    def is_maximized(self) -> bool:
        return self.state == "maximized"

    @property
    def is_minimized(self) -> bool:
        return self.state == "minimized"

    @property
    def is_active(self) -> bool:
        return bool(self.query("isActive"))

    @property
    def window_bounds(self) -> dict[str, Any]:
        """``{"x", "y", "width", "height"}``，窗口在屏幕上的真实位置与尺寸。"""
        return dict(self.query("windowBounds") or {})

    # ---- 窗口动作 ----

    def minimize(self) -> None:
        self._app._send_invoke(self._id, "minimize")

    def maximize(self) -> None:
        self._app._send_invoke(self._id, "maximize")

    def restore(self) -> None:
        self._app._send_invoke(self._id, "restore")

    def activate(self) -> None:
        """把窗口提到前台并激活。"""
        self._app._send_invoke(self._id, "activate")

    def start_move(self) -> None:
        """让窗口跟随鼠标移动，直到松开左键。

        主要给**自绘标题栏**用：嵌入子窗口自带系统标题栏，按住就能拖，
        一般用不到这个方法。只有当窗口藏掉了系统标题栏、自己画一条的时候，
        才需要在自绘条上绑定 ``pointerPressed``::

            bar = ui.Label(win, "⠿  按住这里拖动")
            bar.bind("pointerPressed", lambda e: win.start_move())

        触发时机是「按下」——按下的那一刻把控制权交给系统，之后窗口一直
        跟着鼠标走直到松手，手感和按系统标题栏一致。
        """
        self._app._send_invoke(self._id, "start_move")

    def protocol(self, _name: str, callback: Callable[..., None]) -> None:
        """tkinter 兼容：``protocol("WM_DELETE_WINDOW", fn)``。"""
        self.on_close(callback)

    def __enter__(self) -> "Window":
        return self

    def __exit__(self, *exc_info: object) -> None:
        self._app.quit()

    def __repr__(self) -> str:
        return f"<Window id={self._id} title={self._window_props.get('title')!r}>"
