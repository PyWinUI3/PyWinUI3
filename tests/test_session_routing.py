"""多窗口会话路由回归测试：不再只测协议函数形状，而是测真实发送行为。

覆盖评审指出的三类 P0 串台：
1. 合并窗口里新建的 set / window op 丢了 _session，flush 后全归 main；
2. Widget 没有 _session，子窗口里的普通控件被登记成 main，关窗后节点残留；
3. op_window 不带会话 + 合并键全局共享，改子窗口标题改到主窗口，
   同一帧两个窗口改标题还会并成一条。
"""

from __future__ import annotations

import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "python"))

import pywinui3 as ui  # noqa: E402
from pywinui3 import protocol  # noqa: E402


class FakeHost:
    """只记录出站报文的假宿主，用来断言 patch 实际带哪个 session。"""

    def __init__(self) -> None:
        self.sent: list[dict] = []

    def send(self, message: dict) -> None:
        self.sent.append(message)


class SessionRoutingTest(unittest.TestCase):
    def setUp(self) -> None:
        self.app = ui.App()
        self.host = FakeHost()
        self.app._host = self.host  # type: ignore[assignment]
        self.app._rendered = True
        # 建两扇窗：第一扇是 main，第二扇自动分到 w1
        self.main = ui.Window(self.app, title="主")
        self.sub = ui.Window(self.app, title="子")
        self.host.sent.clear()
        self.app._timers.clear()

    def tearDown(self) -> None:
        self.app._host = None
        self.app._timers.clear()

    def flush(self) -> list[dict]:
        self.app._flush_patches()
        patches = [m for m in self.host.sent if m.get("op") == "patch"]
        self.host.sent.clear()
        return patches

    def test_widget_inherits_master_session(self):
        """普通控件从 master 继承会话，不再一律登记成 main。"""
        label = ui.Label(self.sub, "子窗口里的标签")
        self.assertEqual(label._session, "w1")
        self.assertEqual(self.app._session_of_node[label._id], "w1")
        self.assertIs(self.app._nodes[label._id], label)

    def test_set_op_on_subwindow_routes_to_w1(self):
        """新建的合并 set op 保留 _session，flush 后打到 w1 而非 main。"""
        label = ui.Label(self.sub, "x")
        self.flush()  # 先落地 insert，避免混进断言
        label.configure(text="改了")
        patches = self.flush()
        self.assertEqual(len(patches), 1, patches)
        self.assertEqual(patches[0].get("session"), "w1", patches[0])
        self.assertTrue(any(op.get("t") == "set" for op in patches[0]["ops"]))

    def test_window_configure_routes_to_own_session(self):
        """改子窗口标题只动 w1，不落到 main。"""
        self.sub.configure(title="子-改")
        patches = self.flush()
        w1 = [p for p in patches if p.get("session") == "w1"]
        self.assertEqual(len(w1), 1, patches)
        ops = [op for p in w1 for op in p["ops"] if op.get("t") == "window"]
        self.assertEqual(ops[-1]["props"]["title"], "子-改")

    def test_two_windows_same_frame_not_merged(self):
        """同帧改两个窗口标题，应分成两条 patch，而不是并成一条。"""
        self.main.configure(title="主-改")
        self.sub.configure(title="子-改")
        patches = self.flush()
        sessions = [p.get("session", "main") for p in patches]
        self.assertIn("main", sessions, patches)
        self.assertIn("w1", sessions, patches)
        # 绝不能出现一条 patch 同时带两个窗口的 title
        for p in patches:
            titles = [
                op["props"]["title"]
                for op in p["ops"]
                if op.get("t") == "window" and "title" in op.get("props", {})
            ]
            self.assertLessEqual(len(titles), 1, f"同帧两个窗口标题被并成一条: {p}")

    def test_close_window_cleans_its_nodes(self):
        """关窗后属于该会话的节点被清理，不会残留成 main 的孤儿。"""
        label = ui.Label(self.sub, "会被清掉")
        self.assertIn(label._id, self.app._nodes)
        self.app._dispatch_window_closed({"t": "windowClosed", "session": "w1", "code": 0})
        self.assertNotIn(label._id, self.app._nodes)
        self.assertNotIn(label._id, self.app._session_of_node)
        self.assertTrue(self.sub._destroyed)

    def test_maximize_request_before_render_goes_into_window_spec(self):
        """文档示例：mainloop 前挂回调，maximizeRequestable 要进 render 的 window。"""
        fresh = ui.App()
        win = ui.Window(fresh, title="t")
        win.on_maximize_request(lambda e: None)
        spec = win._window_spec()
        self.assertTrue(spec["maximizeRequestable"])

    def test_respond_certificate_error_shape(self):
        """证书错误回执走 invoke，requestId 与 allow 原样带出。"""
        web = ui.WebView2(self.main, source="https://example.com")
        self.host.sent.clear()
        web.respond_certificate_error("cert1", True)
        invokes = [m for m in self.host.sent if m.get("op") == "invoke"]
        self.assertEqual(len(invokes), 1)
        self.assertEqual(invokes[0]["method"], "respond_certificate_error")
        self.assertEqual(invokes[0]["args"], {"requestId": "cert1", "allow": True})
        # main 会话在协议里省略 session 字段（与旧版报文兼容）
        self.assertNotIn("session", invokes[0])
        self.assertTrue(hasattr(web, "on_certificate_error"))


if __name__ == "__main__":
    unittest.main()
