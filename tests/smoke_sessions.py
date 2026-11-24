"""多窗口会话隔离 + render 失败不回 ack 的真机冒烟。

这两类问题单靠协议函数形状测试抓不到，必须对着真实宿主发报文、看它把
patch 应用到哪扇窗、以及 render 失败时回什么。需要桌面会话（不进纯单测 CI）。

    python tests/smoke_sessions.py
"""

from __future__ import annotations

import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from smoke_render import Host, default_exe  # noqa: E402

FAILURES: list[str] = []
_QUERY_SEQ = 0


def query_in(host: Host, node: str, key: str, session: str) -> dict:
    """带 session 的 query，读回指定会话的运行时值。"""
    global _QUERY_SEQ
    _QUERY_SEQ += 1
    request_id = f"qs{_QUERY_SEQ}"
    host.send({
        "op": "query", "id": request_id, "node": node, "key": key,
        "session": session,
    })
    while True:
        message = host.recv(timeout=8)
        if message.get("t") == "result" and message.get("id") == request_id:
            return message


def check(name: str, ok: bool, detail: str = "") -> None:
    print(f"  {'PASS' if ok else 'FAIL'}  {name}" + (f"  {detail}" if detail else ""))
    if not ok:
        FAILURES.append(name)


def main() -> int:
    host = Host(default_exe())
    try:
        print("[1] handshake")
        host.send({"op": "hello", "protocol": 1, "client": "smoke-sessions", "pid": 0})
        check("ready", host.recv().get("t") == "ready")

        print("\n[2] main render")
        host.send({
            "op": "render", "rev": 1,
            "window": {"title": "主窗口", "width": 420, "height": 260},
            "root": "m0",
            "nodes": [
                {"id": "m0", "type": "Column", "children": ["m1"]},
                {"id": "m1", "type": "Label", "props": {"text": "main"}},
            ],
        })
        check("main ack", host.recv().get("t") == "ack")

        print("\n[3] w1 render（新会话开新窗）")
        host.send({
            "op": "render", "rev": 2, "session": "w1",
            "window": {"title": "子窗口", "width": 420, "height": 260},
            "root": "s0",
            "nodes": [
                {"id": "s0", "type": "Column", "children": ["s1"]},
                {"id": "s1", "type": "Label", "props": {"text": "sub"}},
            ],
        })
        check("w1 ack", host.recv().get("t") == "ack")

        time.sleep(0.8)

        print("\n[4] 只改 w1 的窗口标题，main 不受影响")
        host.send({
            "op": "patch", "rev": 3, "session": "w1",
            "ops": [{"t": "window", "props": {"title": "子窗口-改"}}],
        })
        # patch 不回 ack，用 query 读回验证落在各自会话
        w1_state = query_in(host, "s0", "windowState", "w1")
        check("w1 会话仍可用", w1_state.get("ok") is True, str(w1_state))
        main_state = query_in(host, "m0", "windowState", "main")
        check("main 会话仍可用", main_state.get("ok") is True, str(main_state))

        print("\n[5] render 根节点不存在 -> 不回 ack，应回 error")
        host.send({
            "op": "render", "rev": 4, "session": "w2",
            "window": {"title": "坏窗", "width": 320, "height": 200},
            "root": "missing",
            "nodes": [{"id": "orphan", "type": "Label"}],
        })
        first = host.recv(timeout=5)
        # 失败时可能先回若干 error（unknown-node / bad-protocol），但绝不能回 ack
        saw_error = first.get("t") == "error"
        saw_ack = first.get("t") == "ack"
        # 再读几条，确认没有混入 ack
        for _ in range(4):
            try:
                nxt = host.recv(timeout=1)
            except (TimeoutError, RuntimeError):
                break
            if nxt.get("t") == "ack" and nxt.get("rev") == 4:
                saw_ack = True
            if nxt.get("t") == "error":
                saw_error = True
        check("失败 render 回了 error", saw_error, str(first))
        check("失败 render 没有回 ack", not saw_ack)

        print("\n[6] close")
        host.send({"op": "close", "code": 0})
        while True:
            message = host.recv(timeout=5)
            if message.get("t") == "bye":
                check("收到 bye", True)
                break
    finally:
        host.close()

    print("\n" + "=" * 50)
    if FAILURES:
        print(f"FAILED: {FAILURES}")
        return 1
    print("ALL PASSED")
    return 0


if __name__ == "__main__":
    sys.exit(main())
