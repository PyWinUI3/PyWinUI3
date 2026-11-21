"""多窗口与会话 id：协议报文形状、session 省略规则。"""

import unittest

import pywinui3 as ui
from pywinui3 import protocol


class TestSessionProtocol(unittest.TestCase):
    def test_main_omits_session(self):
        # main 不写 session 字段，保持与旧版宿主完全一样的报文形状
        self.assertNotIn("session", protocol.patch(1, [protocol.op_set("n1", {"text": "x"})]))
        self.assertNotIn(
            "session",
            protocol.render(1, {"title": "T"}, "n1", [protocol.node("n1", "Label", {})]),
        )
        self.assertNotIn("session", protocol.invoke("n1", "focus"))
        self.assertNotIn("session", protocol.query("q1", "n1", "text"))

    def test_non_main_has_session(self):
        self.assertEqual(protocol.patch(1, [], session="w1")["session"], "w1")

        msg = protocol.render(1, {}, "n1", [], session="w2")
        self.assertEqual(msg["session"], "w2")

        self.assertEqual(protocol.invoke("n1", "focus", session="w1")["session"], "w1")
        self.assertEqual(protocol.query("q1", "n1", "text", session="w1")["session"], "w1")

    def test_window_action(self):
        self.assertEqual(
            protocol.window_action("w1", "hide"),
            {"op": "windowAction", "session": "w1", "action": "hide"},
        )
        self.assertEqual(protocol.window_action("main", "show")["action"], "show")

        with self.assertRaises(ValueError):
            protocol.window_action("w1", "destroy")


class TestMultiWindowApi(unittest.TestCase):
    def test_window_session_auto_alloc(self):
        # 需要 App 才能构造 Window，这里只验证协议层与类属性，不构造真实窗口
        self.assertTrue(hasattr(ui.Window, "hide"))
        self.assertTrue(hasattr(ui.Window, "show"))
        self.assertTrue(hasattr(ui.Window, "focus"))
        self.assertTrue(hasattr(ui.Window, "close"))
        self.assertTrue(hasattr(ui.Window, "render"))


if __name__ == "__main__":
    unittest.main()
