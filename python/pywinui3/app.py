"""App —— 宿主进程的生命周期与事件循环。

这个类承担的是 tkinter 里 ``Tk`` 的角色，但把「窗口」这件事拆了出去：
``App`` 管进程与循环，``Window`` 管窗口与控件树。

事件循环的模型刻意做得和 tkinter 一样：主线程阻塞在消息接收上，
回调就在这个线程里执行。所以回调里阻塞会卡住整个 UI，
这一点和 tkinter 完全一致，不做隐藏线程池的魔法。
"""

from __future__ import annotations

import collections
import os
import sys
import time
import traceback
from pathlib import Path
from typing import Any, Callable, ClassVar

from . import protocol
from .errors import (
    HostExited,
    HostStartError,
    HostTimeout,
    ProtocolError,
    ProtocolVersionError,
    UiError,
)
from .event import Event
from .transport import HostProcess

#: 握手超时（秒）。宿主冷启动要初始化 WinUI，留足余量。
DEFAULT_TIMEOUT = 10.0

#: 事件循环的单次轮询间隔。足够跟上 60fps，又不会空转烧 CPU。
POLL_INTERVAL = 0.05


def _raise_timer_resolution() -> bool:
    """把 Windows 定时器精度提到 1ms，返回是否真的改了。

    系统默认精度约 15.6ms，``after(16)`` 实际会落到 ~31ms，
    表现就是动画卡在半个帧率上（实测 31fps，提升后 52fps）。
    改的是进程级设置，退出时要还原。
    """
    if sys.platform != "win32":
        return False
    try:
        import ctypes

        return ctypes.windll.winmm.timeBeginPeriod(1) == 0
    except Exception:
        return False


def _restore_timer_resolution(active: bool) -> None:
    if not active:
        return
    try:
        import ctypes

        ctypes.windll.winmm.timeEndPeriod(1)
    except Exception:
        pass

#: 同一帧内的多次属性赋值合并成一条 patch 的时间窗（毫秒）。
#: 见 PLAN「决策三」；不合并的话拖动 Slider 或跑实时 UI 会把管道刷爆。
MERGE_WINDOW_MS = 16


class App:
    """宿主进程与应用生命周期。"""

    _default: ClassVar[App | None] = None

    def __init__(
        self,
        host: str | Path | None = None,
        *,
        popup: bool = False,
        timeout: float = DEFAULT_TIMEOUT,
        on_error: Callable[[UiError], None] | None = None,
        on_warning: Callable[[str, str | None], None] | None = None,
    ):
        self._host_path = host
        self._popup = popup
        self._timeout = timeout
        self._on_error = on_error
        #: 非致命告警（属性名拼错、未知控件/事件）的回调，签名 ``(message, node_id)``。
        #: 未设置时打到 stderr —— 不能像以前那样只留在宿主的临时日志里。
        self._on_warning = on_warning

        self._host: HostProcess | None = None
        self._rev = 0
        self._node_seq = 0
        self._handler_seq = 0
        self._query_seq = 0
        self._file_seq = 0

        self._nodes: dict[str, Any] = {}
        self._handlers: dict[str, Callable[..., Any]] = {}
        self._window: Any = None

        #: 多窗口：session id -> Window。main 是启动时那扇，
        #: 其余由 Window(app, session="xxx") 创建。
        self._windows: dict[str, Any] = {}
        self._session_seq = 0
        #: 节点 id -> 所属会话。render/patch/事件路由都靠它。
        self._session_of_node: dict[str, str] = {}

        # query() 同步等 result 时，中途到达的事件消息不能丢，先存这里，
        # 事件循环下一轮优先派发。回调里调 query 也安全（可重入）。
        # 用 deque：高频事件风暴下 list.pop(0) 是 O(n)，整轮派发会退化成 O(n²)。
        self._inbox: collections.deque[dict[str, Any]] = collections.deque()

        self._rendered = False
        self._running = False
        self._graceful = False
        # token -> (deadline, callback)。用 token 才能取消，和 tkinter 的 after 对齐。
        self._timers: dict[str, tuple[float, Callable[[], None]]] = {}
        self._timer_seq = 0

        # 16ms 合并窗口：待发 patch 按节点聚合，窗口结束时一次性发出。
        # _pending_index 记录 set/window 操作在 _pending 中的位置，便于合并；
        # 遇到 insert/remove/move 会清空索引，避免跨结构变更错误合并。
        self._pending: list[dict[str, Any]] = []
        self._pending_index: dict[str, int] = {}
        self._flush_token: str | None = None

        self._micro_seq = 0
        self._on_micro_done: Callable[[dict[str, Any]], None] | None = None

        #: 宿主回报的能力位，握手后可用。
        self.capabilities: dict[str, Any] = {}

        App._default = self

    # ------------------------------------------------------------------
    # 单例
    # ------------------------------------------------------------------

    @classmethod
    def default(cls) -> App:
        """返回当前默认 App，没有就创建一个。供 ``Window()`` 无参调用时使用。"""
        if cls._default is None:
            cls._default = cls()
        return cls._default

    # ------------------------------------------------------------------
    # 启动与握手
    # ------------------------------------------------------------------

    def start(self) -> None:
        if self._host is not None:
            return

        # 同一实例复用（quit 之后再次 mainloop）时，这些一次性状态必须复位：
        # _graceful 留着 True 会让新会话的关闭流程走错分支，_rendered 留着 True
        # 会让首帧前的属性写入被当成补丁发出去。
        self._graceful = False
        self._running = False
        self._rendered = False
        self._inbox.clear()
        self._pending.clear()
        self._pending_index.clear()
        if self._flush_token is not None:
            self.after_cancel(self._flush_token)
            self._flush_token = None

        host = HostProcess(
            self._host_path,
            extra_args=["--silent"] if self._popup else None,
        )
        host.start()
        self._host = host
        try:
            self._handshake()
        except Exception:
            host.close()
            self._host = None
            raise

    def _handshake(self) -> None:
        assert self._host is not None
        self._host.send(protocol.hello(os.getpid()))

        try:
            message = self._host.recv(timeout=self._timeout)
        except HostTimeout as exc:
            raise HostStartError(
                f"宿主在 {self._timeout:.0f} 秒内没有完成握手，可能卡在 WinUI 初始化"
            ) from exc
        except HostExited as exc:
            detail = f"\n宿主 stderr:\n{exc.stderr}" if exc.stderr else ""
            raise HostStartError(f"宿主在握手前就退出了（exit={exc.exit_code}）{detail}") from exc

        kind = message.get("t")
        if kind == "error":
            raise HostStartError(
                f"宿主拒绝握手：[{message.get('code')}] {message.get('message')}"
            )
        if kind != "ready":
            raise ProtocolError(f"握手时期望收到 ready，实际收到 {kind!r}")

        version = message.get("protocol")
        if version != protocol.PROTOCOL_VERSION:
            raise ProtocolVersionError(
                f"协议版本不匹配：客户端 {protocol.PROTOCOL_VERSION}，宿主 {version}。"
                "请重新构建宿主。"
            )

        self.capabilities = message.get("capabilities") or {}

    # ------------------------------------------------------------------
    # 渲染
    # ------------------------------------------------------------------

    def _attach_window(self, window: Any) -> None:
        session = getattr(window, "_session", "main")
        self._windows[session] = window
        # main 始终是 _window 快捷方式指向的那扇
        if session == "main" or self._window is None:
            self._window = window

    def _render(self, window: Any | None = None) -> None:
        """渲染一扇窗口。不传参数时渲染 main 窗口（保持旧调用方式）。"""
        target = window if window is not None else self._window
        if target is None:
            raise RuntimeError("还没有创建 Window，无法渲染")
        assert self._host is not None

        session = getattr(target, "_session", "main")

        specs: list[dict[str, Any]] = []
        target._collect_specs(specs)

        self._rev += 1
        self._host.send(
            protocol.render(
                self._rev,
                target._window_spec(),
                target._id,
                specs,
                session=session,
            )
        )
        self._rendered = True

    # ------------------------------------------------------------------
    # 事件循环
    # ------------------------------------------------------------------

    def mainloop(self) -> None:
        """启动宿主、渲染窗口、进入事件循环。阻塞直到窗口关闭。"""
        self._graceful = False
        self.start()
        # 动画/拖动依赖 after() 的精度，默认 15.6ms 会让 16ms 间隔落成 31ms
        high_resolution = _raise_timer_resolution()
        try:
            # 渲染所有已创建的窗口：main 先渲染（触发 FirstRenderApplied），
            # 其余会话窗口按创建顺序发送 render，宿主收到后各自建窗。
            self._render()
            for session, window in list(self._windows.items()):
                if session != "main":
                    self._render(window)

            self._loop()
        finally:
            _restore_timer_resolution(high_resolution)
            self._shutdown()

    def _loop(self) -> None:
        assert self._host is not None
        self._running = True

        while self._running:
            # query() 阻塞期间攒下的消息优先派发，保证事件不丢、顺序不乱
            if self._inbox:
                self._dispatch(self._inbox.popleft())
                self._run_timers()
                continue

            try:
                message = self._host.recv(timeout=self._next_timeout())
            except HostTimeout:
                self._run_timers()
                continue
            except HostExited as exc:
                self._running = False
                if self._graceful:
                    break
                detail = f"\n宿主 stderr:\n{exc.stderr}" if exc.stderr else ""
                raise HostExited(
                    f"宿主进程意外退出（exit={exc.exit_code}）{detail}",
                    exc.exit_code,
                    exc.stderr,
                ) from exc

            self._dispatch(message)
            self._run_timers()

    def _dispatch(self, message: dict[str, Any]) -> None:
        kind = message.get("t")
        if kind == "event":
            self._dispatch_event(message)
        elif kind == "error":
            self._dispatch_error(message)
        elif kind == "warning":
            self._dispatch_warning(message)
        elif kind == "microDone":
            self._dispatch_micro_done(message)
        elif kind == "windowClosed":
            self._dispatch_window_closed(message)
        elif kind == "bye":
            self._graceful = True
            self._running = False
        # ack / ready / result 无需在此处理：result 由 query() 自己配对，
        # ack 只在 render 时回一次，rev 只是序号，不做同步等待

    def _dispatch_window_closed(self, message: dict[str, Any]) -> None:
        """某个非 main 窗口被关闭：从窗口表移除，节点也一并清理。"""
        session = str(message.get("session", "main"))
        window = self._windows.pop(session, None)
        if window is None:
            return

        # 这扇窗的所有节点从表里摘掉，避免事件误路由到已销毁的窗口
        for node_id, owner in list(self._session_of_node.items()):
            if owner == session:
                self._nodes.pop(node_id, None)
                del self._session_of_node[node_id]

        window._destroyed = True

    def _dispatch_event(self, message: dict[str, Any]) -> None:
        node = self._nodes.get(str(message.get("id", "")))
        if node is None:
            return

        name = message.get("event")
        if not isinstance(name, str):
            return

        data = message.get("data")
        event = Event(
            widget=node,
            name=name,
            data=data if isinstance(data, dict) else {},
            echo=bool(message.get("echo")),
        )

        # 先同步变量，再决定要不要回调——echo 事件也要同步，否则变量会落后
        node._sync_from_event(event)

        if event.echo:
            return

        node._on_event(event)

        handler_id = node._handler_ids.get(name)
        handler = self._handlers.get(handler_id) if handler_id else None
        if handler is None:
            return

        try:
            handler(event)
        except Exception:  # noqa: BLE001 - 回调异常不能杀掉事件循环
            traceback.print_exc()

    def _dispatch_warning(self, message: dict[str, Any]) -> None:
        """非致命告警：属性名拼错、未知控件类型、事件名写错等。

        默认打到 stderr，这样「拼错一个属性」立刻可见，而不是等界面不生效
        再去翻宿主临时目录里的日志。需要收集或抑制的话传 ``on_warning``。
        """
        text = str(message.get("message", ""))
        node_id = message.get("id")
        if self._on_warning is not None:
            try:
                self._on_warning(text, node_id if isinstance(node_id, str) else None)
            except Exception:  # noqa: BLE001 - 告警回调不该杀掉事件循环
                traceback.print_exc()
            return

        suffix = f" (节点 {node_id})" if isinstance(node_id, str) and node_id else ""
        print(f"[pywinui3][warn] {text}{suffix}", file=sys.stderr)

    def _dispatch_error(self, message: dict[str, Any]) -> None:
        error = UiError(
            str(message.get("code", "internal")),
            str(message.get("message", "")),
            message.get("id"),
        )
        if self._on_error is not None:
            try:
                self._on_error(error)
            except Exception:  # noqa: BLE001
                traceback.print_exc()
        else:
            print(f"[pywinui3] {error}", file=sys.stderr)

    def _dispatch_micro_done(self, message: dict[str, Any]) -> None:
        """微代码执行回执。默认只在失败时留痕，不打断事件循环。"""
        if self._on_micro_done is not None:
            try:
                self._on_micro_done(message)
            except Exception:  # noqa: BLE001
                traceback.print_exc()
        if not message.get("ok", False):
            print(
                f"[pywinui3][micro] {message.get('id', '?')} 执行失败: "
                f"{message.get('error', '未知原因')}",
                file=sys.stderr,
            )

    # ------------------------------------------------------------------
    # 定时器
    # ------------------------------------------------------------------

    def after(self, ms: int, callback: Callable[[], None]) -> str:
        """在 ms 毫秒后执行一次，返回可用于 :meth:`after_cancel` 的 token。

        对应 tkinter 的 ``after``，包括返回 id 这一点——没有取消能力时，
        「收起后延迟清字」这类回调会在状态已经反转后照样触发。
        """
        self._timer_seq += 1
        token = f"a{self._timer_seq}"
        self._timers[token] = (time.monotonic() + ms / 1000.0, callback)
        return token

    def after_cancel(self, token: str) -> None:
        """取消一个还没触发的 ``after``。已触发或不存在时静默忽略。"""
        self._timers.pop(token, None)

    def _next_timeout(self) -> float:
        if not self._timers:
            return POLL_INTERVAL
        nearest = min(deadline for deadline, _ in self._timers.values())
        return max(0.0, min(POLL_INTERVAL, nearest - time.monotonic()))

    def _run_timers(self) -> None:
        if not self._timers:
            return
        now = time.monotonic()
        due = [
            (token, deadline, callback)
            for token, (deadline, callback) in self._timers.items()
            if deadline <= now
        ]
        if not due:
            return

        # 先摘掉再执行：回调里可能重新 after，也可能 cancel 别人
        for token, _, _ in due:
            self._timers.pop(token, None)

        for _, _, callback in sorted(due, key=lambda item: item[1]):
            try:
                callback()
            except Exception:  # noqa: BLE001
                traceback.print_exc()

    # ------------------------------------------------------------------
    # 补丁合并与微代码
    # ------------------------------------------------------------------

    def _merge_pending(self, ops: list[dict[str, Any]]) -> None:
        """把 ops 并入待发队列，同一节点的 set 合并成一个 op。

        只合并连续的、同 id 的 set/window；insert / remove / move 会清空
        合并索引——否则「remove n1 → 重建一个也叫 n1 的节点 → set」会被
        错误地并到被删掉的那个 set 上。
        """
        for op in ops:
            # 内部键：宿主解析 PatchOp 时忽略未知字段，flush 时用它分组
            target_id = op.get("id") or op.get("parent")
            session = self._session_of_node.get(str(target_id), "main") if target_id else "main"
            op = {**op, "_session": session}

            kind = op.get("t")
            if kind == "set":
                key = "set:" + str(op.get("id"))
                index = self._pending_index.get(key)
                if index is not None and self._pending[index].get("t") == "set":
                    target = self._pending[index]
                    target.setdefault("props", {}).update(op.get("props") or {})
                    if "events" in op:
                        target["events"] = op["events"]
                    continue
                merged: dict[str, Any] = {
                    "t": "set",
                    "id": op.get("id"),
                    "props": dict(op.get("props") or {}),
                }
                if "events" in op:
                    merged["events"] = op["events"]
                self._pending_index[key] = len(self._pending)
                self._pending.append(merged)
                continue

            if kind == "window":
                index = self._pending_index.get("window")
                if index is not None and self._pending[index].get("t") == "window":
                    self._pending[index].setdefault("props", {}).update(op.get("props") or {})
                    continue
                self._pending_index["window"] = len(self._pending)
                self._pending.append({"t": "window", "props": dict(op.get("props") or {})})
                continue

            # 结构类操作保持独立、保持顺序
            self._pending_index.clear()
            self._pending.append(op)

    def _flush_patches(self) -> None:
        """立刻发出合并窗口里攒下的 patch，并取消挂着的 flush 定时器。"""
        if self._flush_token is not None:
            self.after_cancel(self._flush_token)
            self._flush_token = None
        if not self._pending:
            return
        ops = self._pending
        self._pending = []
        self._pending_index = {}
        if self._host is None:
            return
        by_session: dict[str, list[dict[str, Any]]] = {}
        for op in ops:
            session = op.pop("_session", "main")
            by_session.setdefault(session, []).append(op)

        for session, group in by_session.items():
            self._rev += 1
            self._host.send(protocol.patch(self._rev, group, session=session))

    def _schedule_flush(self) -> None:
        if self._flush_token is None:
            self._flush_token = self.after(MERGE_WINDOW_MS, self._flush_patches)

    def micro(
        self,
        node_id: str,
        program: list[dict[str, Any]],
        *,
        notify: bool = False,
    ) -> str:
        """把一段受限指令交给宿主在 UI 线程内执行，返回请求 id。

        这是「Python 在 C# 里注入微代码」的入口：宿主按固定语义解释指令，
        不编译、不反射、不碰 IO。典型收益是动画从「每帧一次往返」变成
        「一条消息跑完整段补间」。

        发送前会强制 flush 待发 patch，保证指令看到的是最新状态。
        ``notify=True`` 时宿主执行完回 ``t="microDone"``。
        """
        self._flush_patches()
        if self._host is None:
            raise RuntimeError("宿主尚未启动，无法执行微代码")
        self._micro_seq += 1
        request_id = f"m{self._micro_seq}"
        session = self._session_of_node.get(str(node_id), "main")
        self._host.send(protocol.micro(request_id, node_id, program, notify=notify, session=session))
        return request_id

    # ------------------------------------------------------------------
    # 退出
    # ------------------------------------------------------------------

    def _window_action(self, session: str, action: str) -> None:
        """向宿主发窗口级控制；main 会话省略 session 字段由宿主补默认值。"""
        if self._host is None:
            return
        self._flush_patches()
        self._host.send(protocol.window_action(session, action))

    def quit(self) -> None:
        """请求关闭窗口并退出事件循环。"""
        # 合并窗口里可能还压着最后一次属性更新，关窗前必须落地
        self._flush_patches()
        self._graceful = True
        self._running = False
        if self._host is not None:
            try:
                self._host.send(protocol.close(0))
            except Exception:  # noqa: BLE001 - 退出路径上不抛
                pass

    destroy = quit

    def dialog(self, title: str, body: str, kind: str = "info") -> None:
        """弹出模态对话框。

        会阻塞窗口交互，所以只适合演示和调试；生产界面请用界面内的状态文字。
        同一时刻只能有一个对话框，前一个没关时新的请求会被宿主丢弃。
        """
        if self._host is None:
            return
        self._flush_patches()
        try:
            self._host.send(protocol.dialog(title, body, kind))
        except HostExited:
            pass

    # ------------------------------------------------------------------
    # 运行时读取（query / result）
    # ------------------------------------------------------------------

    def query(
        self,
        node_id: str,
        key: str,
        *,
        timeout: float = 2.0,
        args: dict[str, Any] | None = None,
    ) -> Any:
        """同步读取宿主机上的一个运行时值。

        这是协议里唯一有应答的消息：``invoke`` 只能让宿主做事，
        ``event`` 只在发生时推送，而 ``ActualWidth``、窗口是否最大化、
        文本框选区这类值必须「随时问一句」。

        等待期间收到的事件消息会存进 ``_inbox``，事件循环下一轮照常派发，
        所以在回调里调用也是安全的。宿主没启动或超时会抛异常，
        不会静默返回 ``None``——读不到值和读到空值是两回事。

        ::

            w = ui.Button(win, "确定")
            win.after(1000, lambda: print(w.actual_width, app.query(w._id, "text")))
        """
        host = self._host
        if host is None:
            raise RuntimeError("宿主尚未启动，无法查询运行时状态")

        # 同步读回必须看到最新状态：合并窗口里压着的 patch 先发出去。
        # 少了这一步，query() 会读到上一次 flush 之前的旧值，而且很难察觉。
        self._flush_patches()

        self._query_seq += 1
        request_id = f"q{self._query_seq}"
        session = self._session_of_node.get(str(node_id), "main")
        host.send(protocol.query(request_id, node_id, key, args, session=session))

        deadline = time.monotonic() + timeout
        while True:
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                raise HostTimeout(f"读取 \"{key}\" 超时（{timeout:.2f}s）")

            try:
                message = host.recv(timeout=min(remaining, 0.1))
            except HostTimeout:
                continue

            if message.get("t") == "result" and message.get("id") == request_id:
                if not message.get("ok", False):
                    raise UiError("query", str(message.get("error", "查询失败")), node_id)
                return message.get("value")

            if message.get("t") == "bye":
                # 宿主已经退出，继续等下去只会等到超时，把「进程没了」
                # 误报成「读取超时」。
                self._graceful = True
                self._running = False
                raise HostExited("宿主在查询过程中退出", host.exit_code, host.stderr_text)

            self._inbox.append(message)

    def file_dialog(
        self,
        mode: str,
        *,
        title: str | None = None,
        initial_dir: str | None = None,
        filter: str | None = None,
        default_name: str | None = None,
        timeout: float | None = 300.0,
    ) -> list[str]:
        """弹出文件选择对话框，同步返回选中的路径列表。

        宿主用 **系统自带的文件选择器**（FileOpenPicker / FileSavePicker /
        FolderPicker），支持四种 ``mode``：

        - ``"open"``：选单个已存在文件
        - ``"openMultiple"``：多选，返回多个路径
        - ``"save"``：输入文件名保存，文件可以不存在
        - ``"folder"``：选一个目录

        ``filter`` 是分号分隔的扩展名，如 ``"*.txt;*.py"``（也接受
        ``"图片|*.png;*.jpg"`` 这种带标签的写法）；``default_name`` 预填文件名。

        注意系统选择器的限制：``title`` 和 ``initial_dir`` 不会生效（系统窗口
        不支持自定义标题，初始位置由系统记忆），这是换取原生外观和最近位置
        记忆的代价。

        返回路径列表；用户取消返回空列表。默认等 300 秒——模态对话框
        由人操作，不像 query 那样几毫秒就能回。传 ``timeout=None`` 可无限等。

        等待期间到达的事件不丢，暂存到 ``_inbox`` 下一轮派发，所以在回调里
        调用也安全。宿主中途退出抛 ``HostExited``，超时抛 ``HostTimeout``。
        """
        self._flush_patches()
        if self._host is None:
            raise RuntimeError("宿主尚未启动，无法弹出文件对话框")

        self._file_seq += 1
        request_id = f"f{self._file_seq}"
        host = self._host
        host.send(protocol.file_dialog(
            request_id,
            mode,
            title=title,
            initial_dir=initial_dir,
            filter=filter,
            default_name=default_name,
        ))

        deadline = None if timeout is None else time.monotonic() + timeout
        while True:
            remaining = None if deadline is None else deadline - time.monotonic()
            if remaining is not None and remaining <= 0:
                raise HostTimeout(f"文件对话框超时（{timeout}s）")

            try:
                message = host.recv(timeout=min(remaining, 0.1) if remaining is not None else 0.1)
            except HostTimeout:
                continue

            if (message.get("t") == "fileDialogResult"
                    and message.get("id") == request_id):
                if not message.get("ok", False):
                    # 用户取消是正常的空结果，不是错误；只有真正的故障才带 error
                    error = message.get("error")
                    if error and error != "用户取消":
                        raise UiError("fileDialog", str(error))
                    return []
                paths = message.get("paths")
                return [str(p) for p in paths] if isinstance(paths, list) else []

            if message.get("t") == "bye":
                self._graceful = True
                self._running = False
                raise HostExited("宿主在文件对话框期间退出", host.exit_code, host.stderr_text)

            self._inbox.append(message)

    def _shutdown(self) -> None:
        self._flush_patches()
        host = self._host
        self._host = None
        self._rendered = False
        self._running = False

        # 默认单例不能继续指向已经关掉的 App：否则 Window() 会拿到死实例，
        # 新建控件的补丁被静默丢弃（不报错也不显示）。
        if App._default is self:
            App._default = None

        if host is None:
            return
        if not self._graceful:
            try:
                host.send(protocol.close(0))
            except Exception:  # noqa: BLE001
                pass
        host.close()

    # ------------------------------------------------------------------
    # 内部注册表（供 Widget 调用）
    # ------------------------------------------------------------------

    def _alloc_session_id(self) -> str:
        self._session_seq += 1
        return f"w{self._session_seq}"

    def _alloc_node_id(self) -> str:
        node_id = f"n{self._node_seq}"
        self._node_seq += 1
        return node_id

    def _register_node(self, widget: Any) -> None:
        self._nodes[widget._id] = widget
        session = getattr(widget, "_session", "main")
        self._session_of_node[widget._id] = session

    def _unregister_node(self, node_id: str) -> None:
        self._nodes.pop(node_id, None)
        self._session_of_node.pop(node_id, None)

    def _register_handler(self, handler: Callable[..., Any]) -> str:
        self._handler_seq += 1
        handler_id = f"h{self._handler_seq}"
        self._handlers[handler_id] = handler
        return handler_id

    def _unregister_handler(self, handler_id: str) -> None:
        self._handlers.pop(handler_id, None)

    def _on_node_added(self, parent: Any, child: Any) -> None:
        """控件树变化时同步给宿主。首次渲染前只改本地状态，不发消息。"""
        if not self._rendered or self._host is None:
            return
        index = len(parent._children) - 1
        ops: list[dict[str, Any]] = []
        child._collect_insert_ops(parent._id, index, ops)
        self._send_patch(ops)

    def _send_patch(self, ops: list[dict[str, Any]]) -> None:
        """进入 16ms 合并窗口，而不是立刻发一条消息。

        ``query`` / ``invoke`` / ``dialog`` / ``close`` / ``micro`` 之前都会
        强制 flush，保证「先改属性再读回来」的因果顺序不被窗口打乱。
        """
        if not ops or not self._rendered or self._host is None:
            return
        self._merge_pending(ops)
        self._schedule_flush()

    def _send_invoke(self, node_id: str, method: str, args: dict[str, Any] | None = None) -> None:
        if self._host is None:
            return
        # invoke 常紧跟在 insert / set 之后（例如插入节点后立刻 focus），
        # 不 flush 的话宿主会先执行 invoke、再看到节点，报 unknown-node。
        self._flush_patches()
        session = self._session_of_node.get(str(node_id), "main")
        self._host.send(protocol.invoke(node_id, method, args, session=session))

    # ------------------------------------------------------------------
    # 上下文管理
    # ------------------------------------------------------------------

    def __enter__(self) -> App:
        self.start()
        return self

    def __exit__(self, *exc_info: object) -> None:
        self._shutdown()
