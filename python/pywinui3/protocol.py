"""协议编解码。

本模块是 docs/protocol.md 在 Python 侧的实现，必须与 C# 的
``PyWinUI.Host.Protocol`` 完全对称。改动任何字段都要同步改两端。

设计上刻意把「消息构造」做成普通函数而不是类：消息就是 dict，
序列化只有一种正确形态，这样抓到的管道日志和代码一一对应，排查成本最低。
"""

from __future__ import annotations

import json
from typing import Any, Iterable

PROTOCOL_VERSION = 1
CLIENT_ID = "pywinui3/1.0.0rc2"

#: 单行消息字节上限，与 C# 侧 Wire.MaxLineBytes 一致。
MAX_LINE_BYTES = 8 * 1024 * 1024

def _encode(obj: Any) -> str:
    """序列化一条消息。

    ``allow_nan=False`` 是硬性要求：NaN / Infinity 不是合法 JSON，
    小写 ``NaN`` 会被宿主当成语法错误整行丢弃，甚至（旧版）直接关掉会话。
    与其让一个 ``float('nan')`` 走到管道里，不如在这里立刻炸出调用栈。
    """
    try:
        return json.dumps(
            obj,
            ensure_ascii=False,
            separators=(",", ":"),
            allow_nan=False,
        )
    except ValueError as exc:
        raise ValueError(
            "消息里有 NaN / Infinity，无法编码成 JSON（检查 Slider / NumberBox / 坐标等数值）"
        ) from exc


def encode(message: dict[str, Any]) -> bytes:
    """把消息编码成一行字节（含换行符），可直接写入管道。"""
    line = _encode(message).encode("utf-8")
    if len(line) > MAX_LINE_BYTES:
        raise ValueError(f"消息长度 {len(line)} 超过上限 {MAX_LINE_BYTES}")
    return line + b"\n"


def decode(line: str | bytes) -> dict[str, Any]:
    """解析一行消息。"""
    from .errors import ProtocolError

    if isinstance(line, bytes):
        try:
            line = line.decode("utf-8")
        except UnicodeDecodeError as exc:
            raise ProtocolError(f"消息不是合法 UTF-8: {exc}") from exc

    line = line.strip()
    if not line:
        raise ProtocolError("收到空行")

    try:
        message = json.loads(line)
    except json.JSONDecodeError as exc:
        raise ProtocolError(f"消息不是合法 JSON: {exc}") from exc

    if not isinstance(message, dict):
        raise ProtocolError("消息必须是 JSON 对象")
    return message


# --------------------------------------------------------------------------
# Python -> Host
# --------------------------------------------------------------------------


def _session_field(message: dict[str, Any], session: str) -> dict[str, Any]:
    """main 会话不写 session 字段，保持与旧版宿主的报文完全一致。"""
    if session and session != "main":
        message["session"] = session
    return message


def hello(pid: int, client: str = CLIENT_ID) -> dict[str, Any]:
    return {"op": "hello", "protocol": PROTOCOL_VERSION, "client": client, "pid": pid}


def render(
    rev: int,
    window: dict[str, Any],
    root: str,
    nodes: Iterable[dict[str, Any]],
    session: str = "main",
) -> dict[str, Any]:
    return _session_field({
        "op": "render",
        "rev": rev,
        "window": window,
        "root": root,
        "nodes": list(nodes),
    }, session)


def patch(rev: int, ops: Iterable[dict[str, Any]], session: str = "main") -> dict[str, Any]:
    return _session_field({"op": "patch", "rev": rev, "ops": list(ops)}, session)


def invoke(
    node_id: str,
    method: str,
    args: dict[str, Any] | None = None,
    session: str = "main",
) -> dict[str, Any]:
    message: dict[str, Any] = {"op": "invoke", "id": node_id, "method": method}
    if args:
        message["args"] = args
    return _session_field(message, session)


def query(
    request_id: str,
    node_id: str,
    key: str,
    args: dict[str, Any] | None = None,
    session: str = "main",
) -> dict[str, Any]:
    """向宿主读一个运行时值。宿主回一条 ``t="result"`` 的消息配对。

    与 ``invoke`` 的区别：invoke 是单向命令，query 有应答。
    ``request_id`` 由调用方生成，允许并发时区分不同请求。
    """
    message: dict[str, Any] = {
        "op": "query",
        "id": request_id,
        "node": node_id,
        "key": key,
    }
    if args:
        message["args"] = args
    return _session_field(message, session)


def dialog(
    title: str, body: str, kind: str = "info", session: str = "main"
) -> dict[str, Any]:
    """弹出模态对话框。会阻塞窗口交互，适合调试与演示。"""
    return _session_field(
        {"op": "dialog", "title": title, "body": body, "kind": kind}, session
    )


def file_dialog(
    request_id: str,
    mode: str,
    *,
    title: str | None = None,
    initial_dir: str | None = None,
    filter: str | None = None,
    default_name: str | None = None,
    session: str = "main",
) -> dict[str, Any]:
    """打开文件选择对话框（系统 IFileDialog，支持自定义标题）。

    ``mode`` 取 ``open`` / ``openMultiple`` / ``save`` / ``folder``。
    宿主回一条 ``t="fileDialogResult"`` 的消息，用 ``id`` 配对。
    """
    modes = {"open", "openMultiple", "save", "folder"}
    if mode not in modes:
        raise ValueError(f"未知的文件对话框模式: {mode!r}，可选 {sorted(modes)}")
    message: dict[str, Any] = {"op": "fileDialog", "id": request_id, "mode": mode}
    if title is not None:
        message["title"] = title
    if initial_dir is not None:
        message["initialDir"] = initial_dir
    if filter is not None:
        message["filter"] = filter
    if default_name is not None:
        message["defaultName"] = default_name
    return _session_field(message, session)


def tray(action: str, icon_id: str, spec: dict[str, Any] | None = None) -> dict[str, Any]:
    """托盘图标操作：create / update / destroy。"""
    if action not in ("create", "update", "destroy"):
        raise ValueError(f"未知的托盘动作: {action!r}")
    msg: dict[str, Any] = {"op": "tray", "action": action, "id": icon_id}
    if spec is not None:
        msg["spec"] = spec
    return msg


def window_action(session: str, action: str) -> dict[str, Any]:
    """窗口级控制：hide / show / focus / close。

    与 ``close`` 不同，这里只操作指定会话的那一扇窗，不结束整个宿主。
    """
    if action not in ("hide", "show", "focus", "close"):
        raise ValueError(f"未知的窗口动作: {action!r}")
    return {"op": "windowAction", "session": session, "action": action}


def close(code: int = 0) -> dict[str, Any]:
    return {"op": "close", "code": code}


# --------------------------------------------------------------------------
# patch 操作构造
# --------------------------------------------------------------------------


def op_set(
    node_id: str,
    props: dict[str, Any] | None = None,
    events: dict[str, str] | None = None,
) -> dict[str, Any]:
    """设置节点属性；``events`` 非 None 时整体替换该节点的事件绑定。"""
    op: dict[str, Any] = {"t": "set", "id": node_id, "props": props or {}}
    if events is not None:
        op["events"] = events
    return op


def op_insert(parent: str, index: int, node: dict[str, Any]) -> dict[str, Any]:
    return {"t": "insert", "parent": parent, "index": index, "node": node}


def op_remove(node_id: str) -> dict[str, Any]:
    return {"t": "remove", "id": node_id}


def op_move(node_id: str, parent: str, index: int) -> dict[str, Any]:
    return {"t": "move", "id": node_id, "parent": parent, "index": index}


def op_window(props: dict[str, Any]) -> dict[str, Any]:
    return {"t": "window", "props": props}


# --------------------------------------------------------------------------
# 微代码（宿主内执行的受限指令）
# --------------------------------------------------------------------------

#: 单条 program 允许的指令数上限。与 C# MicroRuntime.MaxOps 一致。
MAX_MICRO_OPS = 64

#: 指令白名单。宿主只认这四个，不做任何动态编译 / 反射 / IO。
MICRO_INSTRUCTIONS = frozenset({"set", "animate", "wait", "emit"})

#: animate 允许改的数值属性。范围窄是为了不让微代码变成任意属性写入口。
MICRO_ANIM_PROPS = frozenset({"width", "height", "opacity"})

#: 单条动画最长时长（毫秒）。与 C# MicroRuntime.MaxDurationMs 一致。
MAX_MICRO_DURATION_MS = 5000


def micro(
    request_id: str,
    node_id: str,
    program: Iterable[dict[str, Any]],
    *,
    notify: bool = False,
    session: str = "main",
) -> dict[str, Any]:
    """把一小段指令交给宿主在 UI 线程内执行，避免逐帧跨进程往返。

    ``program`` 的元素形如 ``{"i": "animate", "prop": "width", "to": 200,
    "ms": 320, "divisor": 15, "steps": 100}``。指令集是白名单，
    不是可执行代码：宿主按固定语义解释，无法访问文件系统或任意 .NET API。

    ``notify=True`` 时宿主执行完回一条 ``t="microDone"``（成功或失败都回）。
    返回 request_id，调用方可据此等待回执。
    """
    ops = list(program)
    if not ops:
        raise ValueError("micro program 不能为空")
    if len(ops) > MAX_MICRO_OPS:
        raise ValueError(f"micro program 最多 {MAX_MICRO_OPS} 条指令")
    for op in ops:
        if op.get("i") not in MICRO_INSTRUCTIONS:
            raise ValueError(f"未知 micro 指令: {op.get('i')!r}")

    message: dict[str, Any] = {
        "op": "micro",
        "id": request_id,
        "node": node_id,
        "program": ops,
    }
    _session_field(message, session)
    if notify:
        message["notify"] = True
    return message


# --------------------------------------------------------------------------
# Host -> Python 的取值辅助
# --------------------------------------------------------------------------


def message_type(message: dict[str, Any]) -> str:
    """返回出站消息的 ``t`` 字段。"""
    value = message.get("t")
    if not isinstance(value, str):
        raise ValueError(f"出站消息缺少 t 字段: {message!r}")
    return value


def node(
    node_id: str,
    type_: str,
    props: dict[str, Any] | None = None,
    children: list[str] | None = None,
    events: dict[str, str] | None = None,
) -> dict[str, Any]:
    """构造一个节点声明，省略空字段以减小报文体积。"""
    spec: dict[str, Any] = {"id": node_id, "type": type_}
    if props:
        spec["props"] = props
    if children:
        spec["children"] = children
    if events:
        spec["events"] = events
    return spec
