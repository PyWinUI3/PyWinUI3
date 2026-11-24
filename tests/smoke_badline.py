"""坏输入容错冒烟：坏 JSON / 错误字段之后，会话必须还活着。

评审指出旧实现把 bad-json / bad-protocol 当致命错误直接 Shutdown(1)，
一个字段写错就整窗消失。这里逐条打进去，最后还能 query 成功才算过。

    python tests/smoke_badline.py
"""

from __future__ import annotations

import sys
from pathlib import Path

# 见 smoke_render.py 同处说明：Windows runner 默认 cp1252，中文标签会抛
# UnicodeEncodeError。独立运行本脚本时也要保证 UTF-8 输出。
if sys.stdout.encoding and sys.stdout.encoding.lower() != "utf-8":
    sys.stdout.reconfigure(encoding="utf-8")

sys.path.insert(0, str(Path(__file__).resolve().parent))

from smoke_render import Host, default_exe  # noqa: E402

FAILURES: list[str] = []


def check(name: str, ok: bool, detail: str = "") -> None:
    print(f"  {'PASS' if ok else 'FAIL'}  {name}" + (f"  {detail}" if detail else ""))
    if not ok:
        FAILURES.append(name)


def main() -> int:
    host = Host(default_exe())
    try:
        print("[1] handshake")
        host.send({"op": "hello", "protocol": 1, "client": "smoke-badline", "pid": 0})
        check("ready", host.recv().get("t") == "ready")

        print("\n[2] 非法 JSON 行")
        host.send_raw("{ this is not json")
        message = host.recv()
        check("回 error 而不是断线", message.get("t") == "error", str(message.get("t")))
        check("错误码 bad-json", message.get("code") == "bad-json", str(message))

        print("\n[3] 字段类型错误的 patch")
        host.send({
            "op": "render", "rev": 1,
            "window": {"title": "badline", "width": 420, "height": 240},
            "root": "n0",
            "nodes": [
                {"id": "n0", "type": "Column", "children": ["n1"]},
                # 不传 value：NumberBox.Value 默认就是 double.NaN
                {"id": "n1", "type": "NumberBox"},
            ],
        })
        ack = host.recv()
        check("render ack", ack.get("t") == "ack", str(ack.get("t")))

        host.send({"op": "patch", "rev": 2, "ops": "not-an-array"})
        bad = host.recv()
        check("patch 错误回 error", bad.get("t") == "error", str(bad.get("t")))

        print("\n[4] 会话仍然可用")
        host.send({"op": "query", "id": "q1", "node": "n0", "key": "windowState"})
        while True:
            message = host.recv(timeout=5)
            if message.get("t") == "result" and message.get("id") == "q1":
                check("query 仍返回 result", message.get("ok") is True, str(message))
                break

        print("\n[4b] NumberBox 空值")
        host.send({"op": "query", "id": "q2", "node": "n1", "key": "value"})
        while True:
            message = host.recv(timeout=5)
            if message.get("t") == "result" and message.get("id") == "q2":
                check("空值 query 成功", message.get("ok") is True, str(message))
                check("NaN 归一化成 null", message.get("value") is None, str(message.get("value")))
                break

        print("\n[5] 非法数值字面量")
        host.send_raw('{"op":"patch","rev":3,"ops":[{"t":"set","id":"n0","props":{"opacity":NaN}}]}')
        message = host.recv(timeout=5)
        check("NaN 行也被拦住", message.get("t") == "error", str(message))

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
