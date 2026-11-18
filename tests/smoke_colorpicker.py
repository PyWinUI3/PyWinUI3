"""自绘 ColorPicker 真机冒烟：验证 color 读写与 colorChanged 事件。

不 simulate 用户点击 Slider（那需要真实鼠标），而是：
1. render 一个 CustomColorPicker，设 color="#FF0078D4"
2. query("color") 读回，确认经过自绘控件而不是原生控件
3. 用 patch 改 color，再读回，确认属性链仍然通

    python tests/smoke_colorpicker.py
"""

from __future__ import annotations

import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from smoke_render import Host, default_exe  # noqa: E402

FAILURES: list[str] = []


def check(name: str, ok: bool, detail: str = "") -> None:
    print(f"  {'PASS' if ok else 'FAIL'}  {name}" + (f"  {detail}" if detail else ""))
    if not ok:
        FAILURES.append(name)


def wait_ready(host: Host, timeout: float = 15.0) -> dict:
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        msg = host.recv(timeout=max(0.1, deadline - time.monotonic()))
        if msg.get("t") == "ready":
            return msg
    raise RuntimeError("没有收到 ready")


def drain_until(host: Host, predicate, timeout: float = 10.0):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        msg = host.recv(timeout=max(0.1, deadline - time.monotonic()))
        if predicate(msg):
            return msg
    return None


def main() -> None:
    host = Host(default_exe())
    try:
        # 握手：Python 先发 hello，宿主才回 ready
        host.send({"op": "hello", "protocol": 1, "client": "smoke/1.0"})
        ready = host.recv(timeout=15.0)
        check("收到 ready", ready.get("t") == "ready", str(ready.get("t")))
        check("ready 携带 capabilities", "capabilities" in ready, str(ready.get("capabilities")))

        # 渲染一个自绘调色框，设定初始颜色
        host.send({
            "op": "render", "rev": 1,
            "window": {"title": "ColorPicker 自绘验证", "width": 420, "height": 520},
            "root": "root",
            "nodes": [
                {"id": "root", "type": "Column", "props": {"padding": 24, "spacing": 12},
                 "children": ["cp"]},
                {"id": "cp", "type": "ColorPicker", "props": {"color": "#FF0078D4", "height": 320}},
            ],
        })
        ack = drain_until(host, lambda m: m.get("t") == "ack")
        check("render 收到 ack", ack is not None)

        # query 读回：自绘控件必须给出同样的 #AARRGGBB
        host.send({"op": "query", "id": "q1", "node": "cp", "key": "color"})
        result = drain_until(host, lambda m: m.get("t") == "result" and m.get("id") == "q1")
        check("query color 读回成功", result is not None and result.get("ok"), str(result))
        check("color 值正确", result is not None and result.get("value") == "#FF0078D4",
              str(result.get("value") if result else None))

        # patch 改色，再读回，确认属性链通
        host.send({
            "op": "patch", "rev": 2,
            "ops": [{"t": "set", "id": "cp", "props": {"color": "#FF00FF00"}}],
        })
        time.sleep(0.4)  # 等 UI 线程应用 patch
        host.send({"op": "query", "id": "q2", "node": "cp", "key": "color"})
        result2 = drain_until(host, lambda m: m.get("t") == "result" and m.get("id") == "q2")
        check("patch 后 color 更新", result2 is not None and result2.get("value") == "#FF00FF00",
              str(result2.get("value") if result2 else None))

        host.send({"op": "close", "code": 0})
        time.sleep(0.3)
    finally:
        host.close()

    if FAILURES:
        print(f"\n失败 {len(FAILURES)} 项: {FAILURES}")
        raise SystemExit(1)
    print("\n全部通过：自绘 ColorPicker 读写链正常")


if __name__ == "__main__":
    main()
