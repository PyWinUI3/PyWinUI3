"""标签元素的真实进程冒烟测试（需要桌面会话，不进 CI）。

验证的是协议层做不到的事：TabPage 的 Content 真的预置了 StackPanel、
PinnedTab 真的落在 TabStripHeader、`select_tab` 真的能切换选中项、
micro 的 set 能改到标签标题。

运行：
    dotnet build host/PyWinUI.Host -c Release
    python tests/smoke_tabs.py
"""

from __future__ import annotations

import argparse
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from smoke_render import Host, default_exe, query_value  # noqa: E402

SVG_ICON = str(Path(__file__).resolve().parents[1] / "examples" / "assets" / "star.svg")

FAILURES: list[str] = []


def check(name: str, ok: bool, detail: str = "") -> None:
    print(f"  {'PASS' if ok else 'FAIL'}  {name}" + (f"  {detail}" if detail else ""))
    if not ok:
        FAILURES.append(name)


def recv_until(host: Host, event: str, timeout: float = 5.0) -> dict:
    """读到指定事件为止，途中的 warning 一律打印出来。"""
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        message = host.recv(timeout=max(0.1, deadline - time.monotonic()))
        kind = message.get("t")
        if kind == event:
            return message
        if kind == "warning":
            print(f"        [warning] {message.get('message')}")
    raise TimeoutError(f"等不到 {event}")


def build_nodes() -> tuple[str, list[dict]]:
    return "n0", [
        {"id": "n0", "type": "Column", "props": {"padding": 24}, "children": ["n1"]},
        {
            "id": "n1", "type": "TabView",
            "props": {"height": 260, "canReorderTabs": True},
            "children": ["n2", "n3", "n4"],
        },
        {
            "id": "n2", "type": "PinnedTab",
            "props": {"text": "首页", "icon": ""},
            "children": ["n7"],
        },
        {
            "id": "n3", "type": "TabPage",
            "props": {"text": "编辑", "closable": True, "minWidth": 160},
            "children": ["n5"],
            "events": {"tabSelected": "h1"},
        },
        {
            "id": "n4", "type": "TabPage",
            # SVG 图标：渲染成功就不会有 warning，失败会在 ack 之前冒出来
            "props": {
                "text": "一个特别长的标签页标题用来验证省略号",
                "closable": True,
                "icon": SVG_ICON,
                "maxWidth": 180,
            },
            "children": ["n6"],
        },
        {"id": "n5", "type": "Label", "props": {"text": "编辑页内容"}},
        {"id": "n6", "type": "Label", "props": {"text": "预览页内容"}},
        {"id": "n7", "type": "Label", "props": {"text": "固定页内容"}},
    ]


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--exe", type=Path, default=None)
    args = parser.parse_args()

    host = Host(args.exe or default_exe())
    try:
        print("[1] handshake")
        host.send({"op": "hello", "protocol": 1, "client": "smoke-tabs", "pid": 0})
        check("收到 ready", host.recv().get("t") == "ready")

        print("\n[2] render 标签树")
        root, nodes = build_nodes()
        host.send({
            "op": "render", "rev": 1,
            "window": {"title": "PyWinUI3 标签测试", "width": 620, "height": 420},
            "root": root,
            "nodes": nodes,
        })
        ack = host.recv()
        check("收到 ack", ack.get("t") == "ack", str(ack.get("t")))
        time.sleep(0.8)

        print("\n[3] 控件类型与挂载")
        kinds = {node: query_value(host, node, "controlType").get("value") for node in ("n1", "n2", "n3")}
        check("TabView 渲染成 TabView", kinds["n1"] == "TabView", str(kinds["n1"]))
        check("PinnedTab 渲染成 TabViewItem", kinds["n2"] == "TabViewItem", str(kinds["n2"]))
        check("TabPage 渲染成 TabViewItem", kinds["n3"] == "TabViewItem", str(kinds["n3"]))
        check(
            "TabPage 能放子控件",
            query_value(host, "n5", "text").get("value") == "编辑页内容",
            str(query_value(host, "n5", "text")),
        )
        check(
            "PinnedTab 也能放子控件",
            query_value(host, "n7", "text").get("value") == "固定页内容",
            str(query_value(host, "n7", "text")),
        )

        # 标签宽度：minWidth / maxWidth 直接作用在 TabViewItem 上
        min_width = query_value(host, "n3", "actualWidth").get("value")
        check(
            "minWidth 生效",
            isinstance(min_width, (int, float)) and min_width >= 159,
            str(min_width),
        )

        max_width = query_value(host, "n4", "actualWidth").get("value")
        check(
            "maxWidth 生效",
            isinstance(max_width, (int, float)) and max_width <= 181,
            str(max_width),
        )

        # Header 现在是 TextBlock，读回 text 仍应是完整标题（不带动省略号）
        long_text = query_value(host, "n4", "text").get("value")
        check(
            "长标题读回原文",
            long_text == "一个特别长的标签页标题用来验证省略号",
            str(long_text),
        )

        print("\n[4] select_tab 切页")
        host.send({"op": "invoke", "id": "n3", "method": "select_tab"})
        selected = recv_until(host, "event")
        check("收到 tabSelected", selected.get("event") == "tabSelected", str(selected.get("event")))
        check("来自被选中的页", selected.get("id") == "n3", str(selected.get("id")))
        check("携带标签文字", (selected.get("data") or {}).get("value") == "编辑", str(selected.get("data")))

        # 已绑定 tabSelected 时由 SelectionChanged 触发，select_tab 不应再补一条
        try:
            extra = host.recv(timeout=0.4)
            check("没有重复的 tabSelected", extra.get("t") != "event", str(extra))
        except TimeoutError:
            check("没有重复的 tabSelected", True)

        print("\n[5] micro 改标签标题")
        host.send({
            "op": "micro", "id": "m1", "node": "n3", "notify": True,
            "program": [{"i": "set", "props": {"text": "编辑（已改）"}}],
        })
        done = recv_until(host, "microDone")
        check("micro 执行成功", done.get("ok") is True, str(done))
        title = query_value(host, "n3", "text").get("value")
        check("标题已更新", title == "编辑（已改）", str(title))

        print("\n[6] micro 动画在宿主内跑完")
        host.send({
            "op": "micro", "id": "m2", "node": "n1", "notify": True,
            "program": [{
                "i": "animate", "prop": "width", "to": 300.0,
                "ms": 240, "steps": 12, "divisor": 3,
            }],
        })
        animated = recv_until(host, "microDone", timeout=8)
        check("动画 microDone ok", animated.get("ok") is True, str(animated))
        width = query_value(host, "n1", "actualWidth").get("value")
        check(
            "宽度已到 300",
            isinstance(width, (int, float)) and abs(width - 300) < 1.5,
            str(width),
        )

        print("\n[7] 固定选项卡选中")
        host.send({"op": "invoke", "id": "n2", "method": "select_tab"})
        pinned = recv_until(host, "event")
        check("固定选项卡回 tabSelected", pinned.get("id") == "n2", str(pinned.get("id")))
        check("文字正确", (pinned.get("data") or {}).get("value") == "首页", str(pinned.get("data")))

        print("\n[8] close")
        host.send({"op": "close", "code": 0})
        check("收到 bye", recv_until(host, "bye").get("t") == "bye")

    finally:
        host.close()

    print("\n" + "=" * 50)
    if FAILURES:
        print(f"FAILED: {len(FAILURES)} 项 -> {FAILURES}")
        return 1
    print("ALL PASSED")
    return 0


if __name__ == "__main__":
    sys.exit(main())
