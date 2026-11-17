"""容错性回归：坏输入、宿主退出、App 复用。

对应外部评审提的几类"静默失败"：
- 非有限数必须在编码阶段就报错，而不是让宿主收到非法 JSON；
- 宿主 EOF 必须持续报 HostExited，不能第一次之后变成"超时"；
- query 等回执时收到 bye 要立刻抛宿主退出；
- App 关闭后默认单例不能继续指向死实例。
"""

from __future__ import annotations

import collections
import sys
import time
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "python"))

import pywinui3 as ui  # noqa: E402
from pywinui3 import protocol  # noqa: E402
from pywinui3.errors import HostExited  # noqa: E402
from pywinui3.transport import _CLOSED, HostProcess  # noqa: E402


class TestEncodeRejectsNonFinite(unittest.TestCase):
    def test_nan_rejected(self):
        with self.assertRaises(ValueError):
            protocol.encode({"op": "set", "props": {"value": float("nan")}})

    def test_infinity_rejected(self):
        with self.assertRaises(ValueError):
            protocol.encode({"op": "set", "props": {"value": float("inf")}})

    def test_finite_still_works(self):
        line = protocol.encode({"op": "set", "props": {"value": 1.5}})
        self.assertTrue(line.endswith(b"\n"))
        self.assertEqual(protocol.decode(line)["props"]["value"], 1.5)

    def test_error_message_is_actionable(self):
        with self.assertRaises(ValueError) as ctx:
            protocol.encode({"value": float("nan")})
        self.assertIn("NaN", str(ctx.exception))


class TestPersistentEof(unittest.TestCase):
    def setUp(self) -> None:
        # 不 start()：只测队列语义，不拉起真实进程
        self.host = HostProcess(Path("does-not-exist.exe"))
        self.host._queue.put(_CLOSED)

    def test_first_recv_raises(self):
        with self.assertRaises(HostExited):
            self.host.recv(timeout=0.05)

    def test_second_recv_also_raises_instead_of_timeout(self):
        with self.assertRaises(HostExited):
            self.host.recv(timeout=0.05)

        started = time.monotonic()
        with self.assertRaises(HostExited):
            self.host.recv(timeout=5.0)
        self.assertLess(time.monotonic() - started, 0.5, "第二次 recv 又退化成等待超时了")


class ByeHost(HostProcess):
    """一条 bye 之后就没有消息的假宿主。

    刻意继承真实的 ``HostProcess`` 而不自带 ``exit_code`` / ``stderr_text``：
    早期这个桩自己定义了这两个属性，恰好掩盖了 ``App.query`` 误读
    ``host.exit_code``（真实类只有 ``_exit_code``）的 bug。桩必须贴近
    真实接口，否则测试全绿也不代表线上路径可用。
    """

    def __init__(self) -> None:
        # 绕过 HostProcess.__init__（它要找宿主 exe）；只继承状态属性访问器。
        self._proc = None
        self._stderr_tail: collections.deque[str] = collections.deque()
        self.sent: list[dict] = []

    def send(self, message: dict) -> None:
        self.sent.append(message)

    def recv(self, timeout: float | None = None) -> dict:
        return {"t": "bye", "code": 0}


class TestQueryOnBye(unittest.TestCase):
    def test_query_raises_host_exited(self):
        app = ui.App()
        host = ByeHost()
        app._host = host  # type: ignore[assignment]

        started = time.monotonic()
        with self.assertRaises(HostExited) as ctx:
            app.query("n1", "text", timeout=5.0)
        self.assertLess(time.monotonic() - started, 0.5, "收到 bye 还在等超时")

        # 异常必须真的从宿主状态里取到字段——这正是 AttributeError 那次
        # 回归漏掉的部分：只断言异常类型，读错属性同样会「抛出」，只是抛错类型。
        self.assertIsNone(ctx.exception.exit_code)
        self.assertEqual(ctx.exception.stderr, "")
        app._host = None


class TestAppLifecycle(unittest.TestCase):
    def test_shutdown_clears_rendered_and_default(self):
        app = ui.App()
        app._rendered = True
        ui.App._default = app

        app._shutdown()

        self.assertFalse(app._rendered)
        self.assertIsNone(ui.App._default, "关闭后单例仍指向死实例")

    def test_shutdown_is_idempotent(self):
        app = ui.App()
        app._shutdown()
        app._shutdown()
        self.assertFalse(app._rendered)


if __name__ == "__main__":
    unittest.main()
