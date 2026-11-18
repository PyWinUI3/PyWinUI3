"""文件对话框协议形状测试。

file_dialog 是新增的「有应答」消息（仿 query/result），这里锁住：
- 四种 mode 的形状与字段映射；
- 非法 mode 在 Python 侧就被挡住；
- App.file_dialog 的配对/取消/超时语义（用假宿主，不拉 WinUI）。
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
from pywinui3.errors import HostTimeout, HostExited  # noqa: E402
from pywinui3.transport import HostProcess  # noqa: E402


class TestFileDialogMessage(unittest.TestCase):
    def test_open_mode(self):
        msg = protocol.file_dialog("f1", "open", title="选择文件", initial_dir=r"D:\data")
        self.assertEqual(msg["op"], "fileDialog")
        self.assertEqual(msg["id"], "f1")
        self.assertEqual(msg["mode"], "open")
        self.assertEqual(msg["title"], "选择文件")
        self.assertEqual(msg["initialDir"], r"D:\data")

    def test_all_four_modes(self):
        for mode in ("open", "openMultiple", "save", "folder"):
            with self.subTest(mode=mode):
                self.assertEqual(protocol.file_dialog("f", mode)["mode"], mode)

    def test_optional_fields_omitted(self):
        msg = protocol.file_dialog("f1", "open")
        self.assertNotIn("title", msg)
        self.assertNotIn("filter", msg)
        self.assertNotIn("defaultName", msg)

    def test_filter_and_default_name(self):
        msg = protocol.file_dialog(
            "f1", "save", filter="*.txt;*.py", default_name="out.txt"
        )
        self.assertEqual(msg["filter"], "*.txt;*.py")
        self.assertEqual(msg["defaultName"], "out.txt")

    def test_invalid_mode_rejected(self):
        with self.assertRaises(ValueError):
            protocol.file_dialog("f1", "bogus")


class FakeHost(HostProcess):
    """按脚本逐条回消息的假宿主，继承真实 HostProcess 以贴近接口。"""

    def __init__(self, replies: list[dict]):
        self._proc = None
        self._stderr_tail: collections.deque[str] = collections.deque()
        self._replies = collections.deque(replies)
        self.sent: list[dict] = []

    def send(self, message: dict) -> None:
        self.sent.append(message)

    def recv(self, timeout: float | None = None) -> dict:
        if self._replies:
            return self._replies.popleft()
        raise HostTimeout("no more replies")


class TestFileDialogRoundTrip(unittest.TestCase):
    def _app_with(self, replies):
        app = ui.App()
        app._host = FakeHost(replies)  # type: ignore[assignment]
        return app

    def test_open_returns_paths(self):
        app = self._app_with([
            {"t": "fileDialogResult", "id": "f1", "ok": True,
             "paths": [r"D:\a.txt"]},
        ])
        result = app.file_dialog("open")
        self.assertEqual(result, [r"D:\a.txt"])
        self.assertEqual(app._host.sent[0]["mode"], "open")  # type: ignore[attr-defined]
        app._host = None

    def test_multiple_returns_all_paths(self):
        app = self._app_with([
            {"t": "fileDialogResult", "id": "f1", "ok": True,
             "paths": [r"D:\a.txt", r"D:\b.txt"]},
        ])
        self.assertEqual(len(app.file_dialog("openMultiple")), 2)
        app._host = None

    def test_cancel_returns_empty(self):
        app = self._app_with([
            {"t": "fileDialogResult", "id": "f1", "ok": False, "error": "用户取消"},
        ])
        self.assertEqual(app.file_dialog("open"), [])
        app._host = None

    def test_unrelated_messages_stashed_then_matched(self):
        # 对话框还开着时 arrival 的事件不能丢，要进 _inbox
        app = self._app_with([
            {"t": "event", "id": "n1", "event": "click", "data": {}},
            {"t": "fileDialogResult", "id": "f1", "ok": True, "paths": ["x"]},
        ])
        self.assertEqual(app.file_dialog("open"), ["x"])
        self.assertEqual(len(app._inbox), 1, "等待期间的事件应暂存")
        app._host = None

    def test_timeout(self):
        app = self._app_with([])
        with self.assertRaises(HostTimeout):
            app.file_dialog("open", timeout=0.1)
        app._host = None

    def test_host_exit(self):
        app = self._app_with([{"t": "bye", "code": 0}])
        with self.assertRaises(HostExited):
            app.file_dialog("open", timeout=1.0)
        app._host = None


if __name__ == "__main__":
    unittest.main()
