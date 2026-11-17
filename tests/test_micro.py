"""微代码（micro）协议与客户端 API 的单元测试。

micro 是「Python 往 C# 里注入一段受限指令」的入口：宿主按固定语义解释，
不编译、不反射、不碰 IO。这里覆盖三件事：

1. 形状与限额在 Python 侧就被挡住，坏程序不会发给宿主；
2. 发送前强制 flush，保证指令看到的是最新属性；
3. 回执（microDone）能正确派发，失败不打断事件循环。
"""

from __future__ import annotations

import contextlib
import io
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "python"))

import pywinui3 as ui  # noqa: E402
from pywinui3 import protocol  # noqa: E402


class FakeHost:
    def __init__(self) -> None:
        self.sent: list[dict] = []

    def send(self, message: dict) -> None:
        self.sent.append(message)


class MicroTestCase(unittest.TestCase):
    def setUp(self) -> None:
        self.app = ui.App()
        self.host = FakeHost()
        self.app._host = self.host  # type: ignore[assignment]
        self.app._rendered = True
        self.win = ui.Window(self.app, title="micro", width=400, height=300)
        self.host.sent.clear()

    def tearDown(self) -> None:
        self.app._host = None
        self.app._timers.clear()

    def settle(self) -> None:
        """把建树时压下的 insert 先落地，避免它混进被测消息序列。"""
        self.app._flush_patches()
        self.host.sent.clear()


class TestProtocolMicro(unittest.TestCase):
    def test_shape(self):
        message = protocol.micro("m1", "n2", [{"i": "wait", "ms": 10}])
        self.assertEqual(
            message,
            {
                "op": "micro",
                "id": "m1",
                "node": "n2",
                "program": [{"i": "wait", "ms": 10}],
            },
        )

    def test_notify_omitted_by_default(self):
        self.assertNotIn("notify", protocol.micro("m1", "n2", [{"i": "wait", "ms": 1}]))

    def test_notify_true_is_sent(self):
        message = protocol.micro("m1", "n2", [{"i": "wait", "ms": 1}], notify=True)
        self.assertTrue(message["notify"])

    def test_empty_program_rejected(self):
        with self.assertRaises(ValueError):
            protocol.micro("m1", "n2", [])

    def test_too_many_ops_rejected(self):
        program = [{"i": "wait", "ms": 1}] * (protocol.MAX_MICRO_OPS + 1)
        with self.assertRaises(ValueError):
            protocol.micro("m1", "n2", program)

    def test_unknown_instruction_rejected(self):
        with self.assertRaises(ValueError):
            protocol.micro("m1", "n2", [{"i": "eval", "code": "System.IO.File.Delete(...)"}])

    def test_all_whitelisted_instructions_accepted(self):
        program = [
            {"i": "set", "props": {"text": "x"}},
            {"i": "animate", "prop": "width", "to": 100},
            {"i": "wait", "ms": 8},
            {"i": "emit", "event": "click"},
        ]
        message = protocol.micro("m1", "n2", program)
        self.assertEqual(len(message["program"]), 4)

    def test_limits_match_host(self):
        # 这三个数字同时写在 MicroRuntime.MaxOps / MaxDurationMs 里，
        # 改一处必须改两处，否则 Python 放行、宿主拒绝，排查成本很高。
        self.assertEqual(protocol.MAX_MICRO_OPS, 64)
        self.assertEqual(protocol.MAX_MICRO_DURATION_MS, 5000)
        self.assertEqual(protocol.MICRO_ANIM_PROPS, {"width", "height", "opacity"})


class TestAppMicro(MicroTestCase):
    def test_returns_incrementing_request_ids(self):
        first = self.app.micro(self.win._id, [{"i": "wait", "ms": 1}])
        second = self.app.micro(self.win._id, [{"i": "wait", "ms": 1}])
        self.assertEqual((first, second), ("m1", "m2"))

    def test_flushes_pending_patch_first(self):
        label = ui.Label(self.win, "a")
        self.settle()
        label.text = "b"  # 还压在合并窗口里
        self.app.micro(label._id, [{"i": "wait", "ms": 1}])

        self.assertEqual([m["op"] for m in self.host.sent], ["patch", "micro"])
        self.assertEqual(self.host.sent[0]["ops"][0]["props"], {"text": "b"})

    def test_micro_before_host_start_raises(self):
        app = ui.App()
        with self.assertRaises(RuntimeError):
            app.micro("n0", [{"i": "wait", "ms": 1}])

    def test_widget_micro_uses_widget_id(self):
        panel = ui.Column(self.win, width=100)
        self.host.sent.clear()
        panel.micro([{"i": "animate", "prop": "width", "to": 200, "ms": 100}])
        self.assertEqual(self.host.sent[-1]["node"], panel._id)

    def test_animate_width_shape(self):
        panel = ui.Column(self.win, width=200)
        self.host.sent.clear()
        panel.animate_width(56, steps=20, interval=16)

        message = self.host.sent[-1]
        self.assertEqual(message["op"], "micro")
        instruction = message["program"][0]
        self.assertEqual(instruction["i"], "animate")
        self.assertEqual(instruction["prop"], "width")
        self.assertEqual(instruction["to"], 56.0)
        self.assertEqual(instruction["ms"], 320)
        self.assertEqual(instruction["steps"], 20)
        self.assertEqual(instruction["divisor"], 3)

    def test_animate_width_updates_local_props(self):
        panel = ui.Column(self.win, width=200)
        panel.animate_width(56)
        self.assertEqual(panel._props["width"], 56.0)


class TestMicroDone(MicroTestCase):
    def test_callback_receives_message(self):
        seen: list[dict] = []
        self.app._on_micro_done = seen.append
        self.app._dispatch({"t": "microDone", "id": "m1", "ok": True})
        self.assertEqual(seen, [{"t": "microDone", "id": "m1", "ok": True}])

    def test_callback_exception_does_not_escape(self):
        def boom(_: dict) -> None:
            raise RuntimeError("boom")

        self.app._on_micro_done = boom
        with contextlib.redirect_stderr(io.StringIO()):
            self.app._dispatch({"t": "microDone", "id": "m1", "ok": True})  # 不应抛出

    def test_failure_without_callback_is_not_fatal(self):
        # 默认实现只往 stderr 打一行，不应改变运行状态
        self.app._running = True
        with contextlib.redirect_stderr(io.StringIO()):
            self.app._dispatch({"t": "microDone", "id": "m1", "ok": False, "error": "坏指令"})
        self.assertTrue(self.app._running)


if __name__ == "__main__":
    unittest.main()
